using CortexPlexus.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Threading.Channels;

namespace CortexPlexus.App.Indexing;

public sealed class IndexingWorker(
    Channel<IndexingJob> jobChannel,
    IServiceScopeFactory scopeFactory,
    ILogger<IndexingWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("IndexingWorker started. Waiting for jobs...");

        await foreach (var job in jobChannel.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                logger.LogInformation("Processing indexing job: {FilePath} ({Change})", job.FilePath, job.ChangeType);

                using var scope = scopeFactory.CreateScope();
                var pipeline = scope.ServiceProvider.GetRequiredService<IndexingPipeline>();

                // For file-level changes, re-index the containing project
                var projectPath = FindContainingProject(job.FilePath) ?? Path.GetDirectoryName(job.FilePath)!;
                await pipeline.IndexAsync(projectPath, stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to process indexing job: {FilePath}", job.FilePath);
            }
        }
    }

    private static string? FindContainingProject(string filePath)
    {
        var dir = Path.GetDirectoryName(filePath);
        while (dir is not null)
        {
            if (Directory.GetFiles(dir, "*.csproj").Length > 0)
                return dir;
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }
}
