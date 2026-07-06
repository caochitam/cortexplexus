using CortexPlexus.Core.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Threading.Channels;

namespace CortexPlexus.App.Watching;

public sealed class FileWatcherService(
    Channel<IndexingJob> jobChannel,
    ILogger<FileWatcherService> logger) : BackgroundService
{
    private FileSystemWatcher? _watcher;
    private readonly TimeSpan _debounceDelay = TimeSpan.FromSeconds(2);
    private readonly Dictionary<string, DateTime> _lastEvents = new();

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var watchPath = Environment.GetEnvironmentVariable("Workspace__Path") ?? Directory.GetCurrentDirectory();

        if (!Directory.Exists(watchPath))
        {
            logger.LogWarning("Watch path does not exist: {Path}", watchPath);
            return Task.CompletedTask;
        }

        logger.LogInformation("Watching for file changes in {Path}", watchPath);

        _watcher = new FileSystemWatcher(watchPath)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName
        };

        // Watch all supported file types
        _watcher.Filters.Add("*.cs");
        _watcher.Filters.Add("*.ts");
        _watcher.Filters.Add("*.tsx");
        _watcher.Filters.Add("*.js");
        _watcher.Filters.Add("*.jsx");
        _watcher.Filters.Add("*.py");

        _watcher.Changed += OnFileChanged;
        _watcher.Created += OnFileChanged;
        _watcher.Deleted += OnFileChanged;
        _watcher.EnableRaisingEvents = true;

        stoppingToken.Register(() =>
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Dispose();
        });

        return Task.CompletedTask;
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        // Skip build/dependency directories
        if (IsInExcludedDirectory(e.FullPath))
            return;

        // Debounce: ignore events within 2 seconds of last event for same file
        lock (_lastEvents)
        {
            if (_lastEvents.TryGetValue(e.FullPath, out var last) &&
                DateTime.UtcNow - last < _debounceDelay)
                return;
            _lastEvents[e.FullPath] = DateTime.UtcNow;
        }

        var changeType = e.ChangeType switch
        {
            WatcherChangeTypes.Created => ChangeType.Created,
            WatcherChangeTypes.Deleted => ChangeType.Deleted,
            _ => ChangeType.Modified
        };

        logger.LogDebug("File {ChangeType}: {Path}", changeType, e.FullPath);

        var job = new IndexingJob(e.FullPath, Guid.Empty, changeType);
        jobChannel.Writer.TryWrite(job);
    }

    // Build / dependency / VCS directories whose churn must never trigger a re-index. Crucially
    // includes .next (and other framework build outputs): a running `next dev` / `next build`
    // rewrites .next/**/*.js constantly, which would otherwise re-index in a tight loop.
    private static readonly string[] ExcludedDirSegments =
    {
        "node_modules", ".git", "bin", "obj", "dist", "build", "coverage",
        ".next", ".turbo", ".nuxt", ".svelte-kit", ".angular", ".output",
        "__pycache__", ".venv", "venv", "target", ".gradle", "vendor",
    };

    private static bool IsInExcludedDirectory(string fullPath)
    {
        var normalized = fullPath.Replace('\\', '/');
        foreach (var seg in ExcludedDirSegments)
            if (normalized.Contains($"/{seg}/", StringComparison.Ordinal))
                return true;
        return false;
    }

    public override void Dispose()
    {
        _watcher?.Dispose();
        base.Dispose();
    }
}
