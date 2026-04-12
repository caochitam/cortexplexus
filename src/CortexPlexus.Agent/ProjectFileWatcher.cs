using Microsoft.Extensions.Logging;

namespace CortexPlexus.Agent;

/// <summary>
/// Watches a project directory for source file changes using OS kernel events (ReadDirectoryChangesW on Windows).
/// Debounces changes to avoid rapid-fire re-indexing.
/// CPU/RAM usage when idle: ~0%.
/// </summary>
public sealed class ProjectFileWatcher : IDisposable
{
    private static readonly string[] WatchedExtensions = [".cs", ".ts", ".tsx", ".js", ".jsx", ".py", ".md"];

    private static readonly string[] ExcludedDirs =
        ["bin", "obj", "node_modules", "__pycache__", ".venv", ".git", ".vs", ".idea", "dist", "build", "out"];

    private readonly FileSystemWatcher _watcher;
    private readonly ILogger _logger;
    private readonly HashSet<string> _pendingChanges = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();
    private readonly TimeSpan _debounceDelay = TimeSpan.FromSeconds(3);
    private CancellationTokenSource? _debounceCts;

    public ProjectFileWatcher(string path, ILogger logger)
    {
        _logger = logger;
        _watcher = new FileSystemWatcher(path)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime,
            EnableRaisingEvents = false
        };

        _watcher.Changed += OnFileChanged;
        _watcher.Created += OnFileChanged;
        _watcher.Renamed += OnFileRenamed;
        _watcher.Deleted += OnFileChanged;
        _watcher.Error += OnError;
    }

    public async Task WatchAsync(Func<IReadOnlyList<string>, Task> onBatchChanged, CancellationToken ct)
    {
        _watcher.EnableRaisingEvents = true;

        // Store callback for debounce handler
        _onBatchChanged = onBatchChanged;

        try
        {
            // Keep running until cancelled
            await Task.Delay(Timeout.Infinite, ct);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("File watcher stopped.");
        }
        finally
        {
            _watcher.EnableRaisingEvents = false;
        }
    }

    private Func<IReadOnlyList<string>, Task>? _onBatchChanged;

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        if (!IsWatchedFile(e.FullPath)) return;
        QueueChange(e.FullPath);
    }

    private void OnFileRenamed(object sender, RenamedEventArgs e)
    {
        if (IsWatchedFile(e.FullPath)) QueueChange(e.FullPath);
        if (IsWatchedFile(e.OldFullPath)) QueueChange(e.OldFullPath);
    }

    private void OnError(object sender, ErrorEventArgs e)
    {
        _logger.LogWarning(e.GetException(), "FileSystemWatcher error");
    }

    private void QueueChange(string filePath)
    {
        lock (_lock)
        {
            _pendingChanges.Add(filePath);

            // Reset debounce timer
            _debounceCts?.Cancel();
            _debounceCts?.Dispose();
            _debounceCts = new CancellationTokenSource();
            var token = _debounceCts.Token;

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(_debounceDelay, token);
                    await FlushChangesAsync();
                }
                catch (OperationCanceledException)
                {
                    // Debounce reset — ignore
                }
            });
        }
    }

    private async Task FlushChangesAsync()
    {
        IReadOnlyList<string> changes;
        lock (_lock)
        {
            if (_pendingChanges.Count == 0) return;
            changes = _pendingChanges.ToList();
            _pendingChanges.Clear();
        }

        if (_onBatchChanged is not null)
        {
            try
            {
                await _onBatchChanged(changes);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing {Count} file changes", changes.Count);
            }
        }
    }

    internal static bool IsWatchedFile(string filePath)
    {
        var ext = Path.GetExtension(filePath);
        if (!WatchedExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
            return false;

        // Exclude build/dependency directories
        var parts = filePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return !parts.Any(p => ExcludedDirs.Contains(p, StringComparer.OrdinalIgnoreCase));
    }

    public void Dispose()
    {
        _watcher.Dispose();
        _debounceCts?.Dispose();
    }
}
