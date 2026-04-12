using System.IO.Compression;
using System.Threading.Channels;
using CortexPlexus.App.Indexing;
using CortexPlexus.Core.Abstractions;
using CortexPlexus.Core.Models;
using CortexPlexus.Search;
using Microsoft.Extensions.DependencyInjection;

namespace CortexPlexus.App.Api;

public static class GraphApiEndpoints
{
    public static IEndpointRouteBuilder MapGraphApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api");

        api.MapGet("/repositories", async (IRepositoryStore repoStore, CancellationToken ct) =>
        {
            var repos = await repoStore.ListAsync(ct);
            return Results.Ok(repos);
        });

        api.MapGet("/graph/{repoId:guid}", async (
            Guid repoId,
            IGraphStore graphStore,
            int? limit,
            string? kind,
            CancellationToken ct) =>
        {
            var kindFilter = kind?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var overview = await graphStore.GetGraphOverviewAsync(
                repoId,
                limit ?? 500,
                kindFilter,
                ct);
            return Results.Ok(overview);
        });

        api.MapGet("/graph/node", async (
            string fqn,
            IGraphStore graphStore,
            int? depth,
            CancellationToken ct) =>
        {
            var overview = await graphStore.GetNodeNeighborsAsync(fqn, depth ?? 1, ct);
            return Results.Ok(overview);
        });

        api.MapGet("/search", async (
            string q,
            HybridQueryRouter queryRouter,
            Guid? repoId,
            int? limit,
            CancellationToken ct) =>
        {
            var request = new SearchRequest(
                Query: q,
                Type: SearchType.Hybrid,
                Limit: limit ?? 20,
                RepoId: repoId
            );
            var results = await queryRouter.SearchAsync(request, ct);
            return Results.Ok(results);
        });

        // --- Remote Indexing API ---

        // Option C: Push changed files → server writes + indexes
        api.MapPost("/index/push", async (
            HttpRequest request,
            IServiceScopeFactory scopeFactory,
            ILogger<IndexingPipeline> logger,
            CancellationToken ct) =>
        {
            if (!request.HasFormContentType)
                return Results.BadRequest(new { error = "Expected multipart/form-data with a zip file" });

            var form = await request.ReadFormAsync(ct);
            var file = form.Files.GetFile("archive");
            var projectName = form["projectName"].FirstOrDefault() ?? "remote-project";

            if (file is null || file.Length == 0)
                return Results.BadRequest(new { error = "Missing 'archive' file (zip)" });

            // Reject oversized uploads (>50MB likely includes bin/obj — suggest git archive)
            const long maxSize = 50 * 1024 * 1024;
            if (file.Length > maxSize)
                return Results.BadRequest(new { error = $"Archive too large ({file.Length / 1024 / 1024}MB, max 50MB). Use 'git archive -o project.zip HEAD' instead of 'zip -r' to exclude bin/obj/node_modules." });

            // Extract to workspace
            var workspacePath = Environment.GetEnvironmentVariable("Workspace__Path") ?? "/workspace";
            var projectPath = Path.Combine(workspacePath, "_remote", projectName);

            if (Directory.Exists(projectPath))
                Directory.Delete(projectPath, recursive: true);
            Directory.CreateDirectory(projectPath);

            await using var stream = file.OpenReadStream();
            ZipFile.ExtractToDirectory(stream, projectPath, overwriteFiles: true);

            logger.LogInformation("Received remote project: {Name} ({Size} bytes) → {Path}",
                projectName, file.Length, projectPath);

            // Index synchronously
            using var scope = scopeFactory.CreateScope();
            var pipeline = scope.ServiceProvider.GetRequiredService<IndexingPipeline>();
            var stats = await pipeline.IndexAsync(projectPath, ct);

            return Results.Ok(new
            {
                project = projectName,
                path = projectPath,
                files = stats.FilesProcessed,
                symbols = stats.SymbolCount,
                relationships = stats.RelationshipCount,
                duration = stats.Duration.TotalSeconds
            });
        }).DisableAntiforgery();

        // Option A: Git clone/pull → index
        api.MapPost("/index/git", async (
            GitIndexRequest gitRequest,
            IServiceScopeFactory scopeFactory,
            ILogger<IndexingPipeline> logger,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(gitRequest.Url))
                return Results.BadRequest(new { error = "Git URL is required" });

            // Validate URL to prevent command injection
            if (!Uri.TryCreate(gitRequest.Url, UriKind.Absolute, out var uri) ||
                uri.Scheme is not ("https" or "http"))
                return Results.BadRequest(new { error = "Invalid Git URL. Only HTTPS URLs are supported." });

            var branch = gitRequest.Branch ?? "main";
            if (branch.Contains(' ') || branch.Contains(';') || branch.Contains('&'))
                return Results.BadRequest(new { error = "Invalid branch name." });

            var workspacePath = Environment.GetEnvironmentVariable("Workspace__Path") ?? "/workspace";
            var repoName = gitRequest.Name ?? ExtractRepoName(gitRequest.Url);
            var repoPath = Path.Combine(workspacePath, "_git", repoName);

            // Clone or pull
            try
            {
                if (Directory.Exists(Path.Combine(repoPath, ".git")))
                {
                    // Pull latest
                    logger.LogInformation("Pulling latest for: {Repo}", repoName);
                    var pullProcess = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "git",
                        Arguments = $"pull",
                        WorkingDirectory = repoPath,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false
                    });
                    if (pullProcess is not null) await pullProcess.WaitForExitAsync(ct);
                }
                else
                {
                    // Fresh clone
                    logger.LogInformation("Cloning: {Url} → {Path}", gitRequest.Url, repoPath);
                    var cloneProcess = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "git",
                        Arguments = $"clone --depth 1 --branch {branch} {gitRequest.Url} {repoPath}",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false
                    });
                    if (cloneProcess is not null) await cloneProcess.WaitForExitAsync(ct);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Git operation failed for: {Url}", gitRequest.Url);
                return Results.BadRequest(new { error = $"Git operation failed: {ex.Message}" });
            }

            if (!Directory.Exists(repoPath))
                return Results.BadRequest(new { error = "Git clone failed — directory not created" });

            // Index
            using var scope = scopeFactory.CreateScope();
            var pipeline = scope.ServiceProvider.GetRequiredService<IndexingPipeline>();
            var stats = await pipeline.IndexAsync(repoPath, ct);

            return Results.Ok(new
            {
                project = repoName,
                path = repoPath,
                gitUrl = gitRequest.Url,
                branch = gitRequest.Branch ?? "main",
                files = stats.FilesProcessed,
                symbols = stats.SymbolCount,
                relationships = stats.RelationshipCount,
                duration = stats.Duration.TotalSeconds
            });
        });

        return app;
    }

    private static string ExtractRepoName(string gitUrl)
    {
        var name = gitUrl.Split('/').LastOrDefault()?.Replace(".git", "") ?? "repo";
        return name;
    }
}

public sealed record GitIndexRequest(
    string Url,
    string? Branch = null,
    string? Name = null
);
