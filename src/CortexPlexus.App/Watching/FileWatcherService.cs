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
        if (e.FullPath.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") ||
            e.FullPath.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") ||
            e.FullPath.Contains($"{Path.DirectorySeparatorChar}node_modules{Path.DirectorySeparatorChar}") ||
            e.FullPath.Contains($"{Path.DirectorySeparatorChar}__pycache__{Path.DirectorySeparatorChar}") ||
            e.FullPath.Contains($"{Path.DirectorySeparatorChar}.venv{Path.DirectorySeparatorChar}"))
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

    public override void Dispose()
    {
        _watcher?.Dispose();
        base.Dispose();
    }
}
