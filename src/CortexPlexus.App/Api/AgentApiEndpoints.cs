using System.Diagnostics;
using System.Text.Json;
using CortexPlexus.App.Api.Dto;
using CortexPlexus.App.Indexing;
using CortexPlexus.Core;
using CortexPlexus.Core.Abstractions;
using CortexPlexus.Core.Models;
using CortexPlexus.Embedding;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CortexPlexus.App.Api;

public static class AgentApiEndpoints
{
    // Version is a const from CortexPlexus.Core.AgentInfo — single source of
    // truth shared with the agent CLI. Bump AgentInfo.Version, not this file.
    private const string AgentVersion = AgentInfo.Version;

    public static IEndpointRouteBuilder MapAgentApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api");

        // --- Agent Download & Version ---

        api.MapGet("/agent/version", () =>
        {
            var workspacePath = Environment.GetEnvironmentVariable("Workspace__Path") ?? "/workspace";
            // Compute SHA256 of each available archive/binary for integrity verification
            var hashes = new Dictionary<string, string>();
            foreach (var rid in new[] { "win-x64", "linux-x64", "osx-x64" })
            {
                var archivePath = Path.Combine(workspacePath, "_agent", $"cortexplexus-agent-{rid}.tar.gz");
                if (File.Exists(archivePath))
                {
                    var bytes = File.ReadAllBytes(archivePath);
                    hashes[rid] = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
                }
            }
            return Results.Ok(new
            {
                version = AgentVersion,
                platforms = new[] { "win-x64", "linux-x64", "osx-x64" },
                sha256 = hashes
            });
        });

        api.MapGet("/agent/download", (string? platform) =>
        {
            var rid = platform ?? DetectPlatform();

            // Validate against allowlist to prevent path traversal
            string[] allowedPlatforms = ["win-x64", "linux-x64", "osx-x64"];
            if (!allowedPlatforms.Contains(rid))
                return Results.BadRequest(new { error = $"Invalid platform '{rid}'. Allowed: {string.Join(", ", allowedPlatforms)}" });

            var workspacePath = Environment.GetEnvironmentVariable("Workspace__Path") ?? "/workspace";
            var agentDir = Path.Combine(workspacePath, "_agent", rid);

            // Try archive first (framework-dependent with all DLLs)
            var archiveName = $"cortexplexus-agent-{rid}.tar.gz";
            var archivePath = Path.Combine(workspacePath, "_agent", archiveName);
            if (File.Exists(archivePath))
            {
                var stream = File.OpenRead(archivePath);
                return Results.File(stream, "application/gzip", archiveName);
            }

            // Fallback: single binary (self-contained)
            var binaryName = rid.StartsWith("win") ? "cortexplexus-agent.exe" : "cortexplexus-agent";
            var binaryPath = Path.Combine(agentDir, binaryName);
            if (File.Exists(binaryPath))
            {
                var stream = File.OpenRead(binaryPath);
                return Results.File(stream, "application/octet-stream", binaryName);
            }

            return Results.NotFound(new { error = $"Agent not found for platform '{rid}'. Available: win-x64, linux-x64" });
        });

        // Install scripts
        api.MapGet("/agent/install.sh", (HttpRequest request) =>
        {
            var serverUrl = $"{request.Scheme}://{request.Host}";
            var script = $"""
                #!/bin/bash
                set -e
                INSTALL_DIR="$HOME/.cortexplexus/agent"
                mkdir -p "$INSTALL_DIR"

                # Detect platform
                OS=$(uname -s | tr '[:upper:]' '[:lower:]')
                ARCH=$(uname -m)
                case "$OS-$ARCH" in
                    linux-x86_64) RID="linux-x64" ;;
                    darwin-x86_64) RID="osx-x64" ;;
                    darwin-arm64) RID="osx-x64" ;;
                    *) echo "Unsupported: $OS-$ARCH"; exit 1 ;;
                esac

                echo "Downloading CortexPlexus Agent ($RID)..."
                curl -sL "{serverUrl}/api/agent/download?platform=$RID" -o "/tmp/cortexplexus-agent.tar.gz"
                tar -xzf /tmp/cortexplexus-agent.tar.gz -C "$INSTALL_DIR"
                rm /tmp/cortexplexus-agent.tar.gz
                chmod +x "$INSTALL_DIR/cortexplexus-agent"

                # Save version
                curl -s "{serverUrl}/api/agent/version" | grep -o '"version":"[^"]*"' | cut -d'"' -f4 > "$INSTALL_DIR/version.txt"

                echo "Installed to $INSTALL_DIR/"
                echo "Run: dotnet $INSTALL_DIR/cortexplexus-agent.dll watch /path/to/project --server {serverUrl}"
                """;
            return Results.Text(script, "text/plain");
        });

        api.MapGet("/agent/install.ps1", (HttpRequest request) =>
        {
            var serverUrl = $"{request.Scheme}://{request.Host}";
            var script = $"""
                $ErrorActionPreference = 'Stop'
                $installDir = "$env:USERPROFILE\.cortexplexus\agent"
                New-Item -ItemType Directory -Force -Path $installDir | Out-Null

                Write-Host "Downloading CortexPlexus Agent (win-x64)..."
                Invoke-WebRequest -Uri "{serverUrl}/api/agent/download?platform=win-x64" -OutFile "$env:TEMP\cortexplexus-agent.tar.gz"
                tar -xzf "$env:TEMP\cortexplexus-agent.tar.gz" -C $installDir
                Remove-Item "$env:TEMP\cortexplexus-agent.tar.gz"

                $version = (Invoke-RestMethod -Uri "{serverUrl}/api/agent/version").version
                Set-Content -Path "$installDir\version.txt" -Value $version

                Write-Host "Installed to $installDir\"
                Write-Host "Run: dotnet $installDir\cortexplexus-agent.dll watch <path> --server {serverUrl}"
                """;
            return Results.Text(script, "text/plain");
        });

        // --- Index Results (receive pre-parsed data from agent) ---

        api.MapPost("/index/results", async (
            IndexResultsRequest request,
            IServiceScopeFactory scopeFactory,
            ILogger<IndexingPipeline> logger,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.ProjectName))
                return Results.BadRequest(new { error = "projectName is required" });

            // Relaxed validation cho chunked uploads: chấp nhận empty symbols nếu request mang
            // relationships hoặc file hashes (intermediate hoặc final chunk).
            // Pure-empty request vẫn bị reject (kẻ tấn công gửi rác).
            if (request.Symbols.Count == 0 && request.Relationships.Count == 0 && request.FileHashes.Count == 0)
                return Results.BadRequest(new { error = "Request must contain at least one of: symbols, relationships, fileHashes" });

            var sw = Stopwatch.StartNew();

            using var scope = scopeFactory.CreateScope();
            var repoStore = scope.ServiceProvider.GetRequiredService<IRepositoryStore>();
            var graphStore = scope.ServiceProvider.GetRequiredService<IGraphStore>();
            var vectorStore = scope.ServiceProvider.GetRequiredService<IVectorStore>();
            var embeddingService = scope.ServiceProvider.GetRequiredService<IEmbeddingService>();
            var secretsScanner = scope.ServiceProvider.GetRequiredService<ISecretsScanner>();
            var embeddingOptions = scope.ServiceProvider.GetRequiredService<IOptions<EmbeddingOptions>>().Value;

            // Register/get repository (use project name as path for remote agents)
            var repoPath = $"_agent/{request.ProjectName}";
            var repo = await repoStore.GetByPathAsync(repoPath, ct)
                ?? await repoStore.RegisterAsync(request.ProjectName, repoPath, ct);

            logger.LogInformation("Receiving index results from agent: {Project} ({Symbols} symbols, {Rels} relationships)",
                request.ProjectName, request.Symbols.Count, request.Relationships.Count);

            // Convert DTOs to domain models
            var symbols = request.Symbols
                .Select(dto => SymbolDtoMapper.ToModel(dto, repo.Id))
                .Where(s => !string.IsNullOrWhiteSpace(s.Fqn))
                .ToList();

            var relationships = request.Relationships
                .Select(SymbolDtoMapper.ToRelationship)
                .Where(r => !string.IsNullOrWhiteSpace(r.FromFqn) && !string.IsNullOrWhiteSpace(r.ToFqn))
                .ToList();

            // Generate AI summaries (optional, requires LLM)
            var summaryGen = scope.ServiceProvider.GetRequiredService<ISummaryGenerator>();
            if (summaryGen.IsEnabled)
            {
                var methods = symbols.OfType<MethodInfo>().Where(m => m.AiSummary is null).ToList();
                if (methods.Count > 0)
                {
                    var items = methods.Select(m => (m.Signature, m.Documentation)).ToList();
                    var summaries = await summaryGen.SummarizeBatchAsync(items, ct);
                    for (var i = 0; i < methods.Count; i++)
                    {
                        if (summaries[i] is not null)
                        {
                            var idx = symbols.IndexOf(methods[i]);
                            if (idx >= 0) symbols[idx] = methods[i] with { AiSummary = summaries[i] };
                        }
                    }
                    logger.LogInformation("Generated {Count} AI summaries", summaries.Count(s => s is not null));
                }
            }

            // Store in graph (timed separately to diagnose perf issues)
            var graphSw = Stopwatch.StartNew();
            await graphStore.UpsertNodesAsync(symbols, ct);
            var nodeTime = graphSw.Elapsed;
            await graphStore.UpsertEdgesAsync(relationships, ct);
            graphSw.Stop();
            logger.LogInformation("Graph upsert: {Nodes} nodes in {NodeTime:F1}s + {Edges} edges in {EdgeTime:F1}s",
                symbols.Count, nodeTime.TotalSeconds, relationships.Count, (graphSw.Elapsed - nodeTime).TotalSeconds);

            // Generate embeddings server-side (from signatures only, no source code)
            var embedSw = Stopwatch.StartNew();
            var embeddable = symbols
                .Where(s => s.Kind is "class" or "method" or "interface" or "struct" or "record" or "function" or "type" or "document" or "section")
                .ToList();

            var embeddings = await EmbeddingBatchHelper.GenerateEmbeddingsAsync(
                embeddable, embeddingService, secretsScanner, logger,
                embeddingOptions.MaxParallelBatches ?? 1, ct);
            embedSw.Stop();

            // Store in vector + FTS — keep the return value so we can surface
            // partial-persist failures to the agent (HTTP 200 is not enough
            // information when the vector path silently drops rows; see issue #1).
            var vectorSw = Stopwatch.StartNew();
            var vectorResult = await vectorStore.UpsertAsync(symbols, embeddings, ct);
            vectorSw.Stop();

            var warnings = new List<string>();
            if (vectorResult.HasFailures)
            {
                warnings.Add(
                    $"vector_upsert: {vectorResult.Failed} of {vectorResult.Total} symbols failed to persist. " +
                    "Check server logs for stack trace. Re-run indexing after resolving the root cause.");
            }

            // Update file hashes
            foreach (var (filePath, hash) in request.FileHashes)
            {
                await repoStore.UpdateFileHashAsync(filePath, repo.Id, hash, ct);
            }
            await repoStore.UpdateLastIndexedAsync(repo.Id, ct);

            sw.Stop();
            logger.LogInformation(
                "Agent index results stored: {Symbols} symbols, {Rels} relationships, {Embeds} embeddings ({VectorOk}/{VectorTotal} persisted) in {Duration:F1}s " +
                "(graph={GraphTime:F1}s, embed={EmbedTime:F1}s, vector={VectorTime:F1}s)",
                symbols.Count, relationships.Count, embeddings.Count,
                vectorResult.Persisted, vectorResult.Total,
                sw.Elapsed.TotalSeconds,
                graphSw.Elapsed.TotalSeconds, embedSw.Elapsed.TotalSeconds, vectorSw.Elapsed.TotalSeconds);

            return Results.Ok(new IndexResultsResponse
            {
                Project = request.ProjectName,
                Symbols = symbols.Count,
                Relationships = relationships.Count,
                Embeddings = embeddings.Count,
                EmbeddingsPersisted = vectorResult.Persisted,
                EmbeddingsFailed = vectorResult.Failed,
                Warnings = warnings,
                DurationSeconds = sw.Elapsed.TotalSeconds
            });
        });

        // --- File Hashes (for incremental sync) ---

        api.MapGet("/index/{projectName}/hashes", async (
            string projectName,
            IRepositoryStore repoStore,
            CancellationToken ct) =>
        {
            var repoPath = $"_agent/{projectName}";
            var repo = await repoStore.GetByPathAsync(repoPath, ct);

            if (repo is null)
                return Results.Ok(new Dictionary<string, string>());

            var hashes = await repoStore.GetFileHashesAsync(repo.Id, ct);
            return Results.Ok(hashes);
        });

        return app;
    }

    private static string DetectPlatform()
    {
        if (OperatingSystem.IsWindows()) return "win-x64";
        if (OperatingSystem.IsMacOS()) return "osx-x64";
        return "linux-x64";
    }
}
