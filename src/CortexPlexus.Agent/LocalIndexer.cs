using System.Diagnostics;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using CortexPlexus.Core.Abstractions;
using CortexPlexus.Core.Models;
using CortexPlexus.Parsing;
using CortexPlexus.Parsing.Markdown;
using CortexPlexus.Parsing.TreeSitter;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CortexPlexus.Agent;

/// <summary>
/// Parses source code locally using Roslyn + Tree-sitter, then sends
/// only metadata (symbols, relationships, file hashes) to the CortexPlexus server.
/// Source code never leaves the local machine.
/// </summary>
public sealed class LocalIndexer
{
    private readonly string _serverUrl;
    private readonly string _projectName;
    private readonly ILogger _logger;
    private readonly HttpClient _httpClient;
    private readonly ICodeParser _roslynParser;
    private readonly TreeSitterCodeParser _treeSitterParser;
    private readonly MarkdownParser _markdownParser;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    // Server-side file hashes for incremental comparison
    private Dictionary<string, string> _serverHashes = new(StringComparer.OrdinalIgnoreCase);

    public LocalIndexer(string serverUrl, string projectName, ILogger logger)
    {
        _serverUrl = serverUrl.TrimEnd('/');
        _projectName = projectName;
        _logger = logger;
        // 30 minutes — large projects (100K+ relationships) can take 5-15 min server-side to persist
        _httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };

        // Build parsing services via DI
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddProvider(NullLoggerProvider.Instance));
        services.AddCortexPlexusParsing();
        var sp = services.BuildServiceProvider();

        _roslynParser = sp.GetRequiredService<ICodeParser>();
        _treeSitterParser = sp.GetRequiredService<TreeSitterCodeParser>();
        _markdownParser = sp.GetRequiredService<MarkdownParser>();
    }

    /// <summary>
    /// Full index of a project directory. Fetches server hashes first for incremental detection.
    /// </summary>
    public async Task IndexAsync(string path)
    {
        var sw = Stopwatch.StartNew();
        _rootPath = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        _logger.LogInformation("Starting full index of {Path}...", path);

        // Fetch existing hashes from server
        await FetchServerHashesAsync();

        // Compute local hashes and find changed files
        var localHashes = ComputeLocalHashes(path);
        var changedFiles = FindChangedFiles(localHashes);

        if (changedFiles.Count == 0 && _serverHashes.Count > 0)
        {
            _logger.LogInformation("No files changed since last index. Skipping.");
            return;
        }

        var isFullIndex = _serverHashes.Count == 0;

        // Parse all languages
        var allSymbols = new List<CodeSymbol>();
        var allRelationships = new List<Relationship>();

        // C# (Roslyn)
        var solutions = FindSolutionsAndProjects(path);
        if (solutions.Count > 0)
        {
            foreach (var solutionPath in solutions)
            {
                ParseResult result;
                if (isFullIndex)
                {
                    _logger.LogInformation("Parsing C#: {Path}...", solutionPath);
                    result = await _roslynParser.ParseSolutionAsync(solutionPath);
                }
                else
                {
                    var csChanged = changedFiles.Where(f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)).ToList();
                    if (csChanged.Count > 0)
                    {
                        _logger.LogInformation("Incremental C#: {Count} files", csChanged.Count);
                        result = await _roslynParser.ParseFilesAsync(csChanged, solutionPath);
                    }
                    else
                    {
                        continue;
                    }
                }
                allSymbols.AddRange(result.Symbols);
                allRelationships.AddRange(result.Relationships);
            }
        }

        // TypeScript/JavaScript/Python (Tree-sitter)
        var tsResult = await _treeSitterParser.ParseSolutionAsync(path);
        if (tsResult.Symbols.Count > 0)
        {
            allSymbols.AddRange(tsResult.Symbols);
            allRelationships.AddRange(tsResult.Relationships);
        }

        // Markdown
        var mdResult = _markdownParser.ParseDirectory(path);
        if (mdResult.Symbols.Count > 0)
        {
            allSymbols.AddRange(mdResult.Symbols);
            allRelationships.AddRange(mdResult.Relationships);
        }

        _logger.LogInformation("Parsed: {Symbols} symbols, {Rels} relationships", allSymbols.Count, allRelationships.Count);

        if (allSymbols.Count == 0)
        {
            _logger.LogWarning("No symbols extracted.");
            return;
        }

        // Send results to server
        await PostResultsAsync(allSymbols, allRelationships, localHashes);

        sw.Stop();
        _logger.LogInformation("Index complete in {Duration:F1}s", sw.Elapsed.TotalSeconds);
    }

    /// <summary>
    /// Incremental index of specific changed files (called by file watcher).
    /// </summary>
    public async Task IndexFilesAsync(string rootPath, IReadOnlyList<string> changedFiles)
    {
        var sw = Stopwatch.StartNew();

        var allSymbols = new List<CodeSymbol>();
        var allRelationships = new List<Relationship>();

        // Group by language
        var csFiles = changedFiles.Where(f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)).ToList();
        var tsFiles = changedFiles.Where(f => IsTreeSitterFile(f)).ToList();
        var mdFiles = changedFiles.Where(f => f.EndsWith(".md", StringComparison.OrdinalIgnoreCase)).ToList();

        // C# incremental
        if (csFiles.Count > 0)
        {
            var solutions = FindSolutionsAndProjects(rootPath);
            if (solutions.Count > 0)
            {
                var result = await _roslynParser.ParseFilesAsync(csFiles, solutions[0]);
                allSymbols.AddRange(result.Symbols);
                allRelationships.AddRange(result.Relationships);
            }
        }

        // Tree-sitter: re-parse whole directory (tree-sitter is fast)
        if (tsFiles.Count > 0)
        {
            var result = await _treeSitterParser.ParseSolutionAsync(rootPath);
            allSymbols.AddRange(result.Symbols);
            allRelationships.AddRange(result.Relationships);
        }

        // Markdown
        if (mdFiles.Count > 0)
        {
            var result = _markdownParser.ParseDirectory(rootPath);
            allSymbols.AddRange(result.Symbols);
            allRelationships.AddRange(result.Relationships);
        }

        if (allSymbols.Count == 0)
        {
            _logger.LogInformation("No symbols extracted from changed files.");
            return;
        }

        // Compute hashes for changed files only
        var hashes = new Dictionary<string, string>();
        foreach (var file in changedFiles)
        {
            if (File.Exists(file))
                hashes[file] = ComputeFileHash(file);
        }

        await PostResultsAsync(allSymbols, allRelationships, hashes);

        sw.Stop();
        _logger.LogInformation("Incremental index: {Symbols} symbols in {Duration:F1}s", allSymbols.Count, sw.Elapsed.TotalSeconds);
    }

    private async Task FetchServerHashesAsync()
    {
        try
        {
            var url = $"{_serverUrl}/api/index/{Uri.EscapeDataString(_projectName)}/hashes";
            var response = await _httpClient.GetAsync(url);

            if (response.IsSuccessStatusCode)
            {
                _serverHashes = await response.Content.ReadFromJsonAsync<Dictionary<string, string>>(JsonOptions)
                    ?? new Dictionary<string, string>();
                _logger.LogInformation("Server has {Count} file hashes", _serverHashes.Count);
            }
            else
            {
                _serverHashes = new Dictionary<string, string>();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch server hashes, will do full index");
            _serverHashes = new Dictionary<string, string>();
        }
    }

    private string _rootPath = "";

    /// <summary>
    /// Threshold để bật chunking. Payloads nhỏ vẫn gửi 1 POST như cũ (faster).
    /// Lớn hơn threshold → chia thành nhiều POST để:
    /// - Tránh single-request HTTP timeout (R12: 18K symbols + 123K edges crash sau 30 phút)
    /// - Cho phép server commit incrementally (resume sau crash)
    /// - Visibility: log progress mỗi chunk
    /// </summary>
    private const int ChunkThreshold = 3000;

    private const int SymbolChunkSize = 2000;
    private const int RelationshipChunkSize = 5000;

    private async Task PostResultsAsync(
        IReadOnlyList<CodeSymbol> symbols,
        IReadOnlyList<Relationship> relationships,
        Dictionary<string, string> fileHashes)
    {
        // Convert to DTOs — use project-relative paths to avoid leaking dev machine paths
        var symbolDtos = symbols
            .Where(s => !string.IsNullOrWhiteSpace(s.Fqn))
            .Select(ToSymbolDto)
            .ToList();

        var relationshipDtos = relationships
            .Where(r => !string.IsNullOrWhiteSpace(r.FromFqn) && !string.IsNullOrWhiteSpace(r.ToFqn))
            .Select(r => new RelationshipDto
            {
                FromFqn = r.FromFqn,
                ToFqn = r.ToFqn,
                Type = r.Type.ToString(),
                Metadata = r.Metadata
            })
            .ToList();

        // Convert absolute file paths to project-relative to avoid leaking dev machine paths
        var relativeHashes = fileHashes.ToDictionary(
            kv => ToRelativePath(kv.Key),
            kv => kv.Value);

        // Small payload → single POST như cũ (avoid chunking overhead)
        if (symbolDtos.Count + relationshipDtos.Count <= ChunkThreshold)
        {
            await PostChunkAsync(symbolDtos, relationshipDtos, relativeHashes, chunkLabel: "single");
            return;
        }

        // Large payload → chunked upload
        _logger.LogInformation(
            "Large payload detected ({Symbols} symbols + {Rels} relationships) — chunking...",
            symbolDtos.Count, relationshipDtos.Count);

        // Phase 1: Symbols in batches of SymbolChunkSize
        var symbolBatches = symbolDtos.Chunk(SymbolChunkSize).ToList();
        for (var i = 0; i < symbolBatches.Count; i++)
        {
            var batch = symbolBatches[i];
            var label = $"symbols {i + 1}/{symbolBatches.Count} ({batch.Length} items)";
            await PostChunkAsync(
                batch.ToList(),
                new List<RelationshipDto>(),
                new Dictionary<string, string>(),
                chunkLabel: label);
        }

        // Phase 2: Relationships in batches of RelationshipChunkSize
        var relBatches = relationshipDtos.Chunk(RelationshipChunkSize).ToList();
        for (var i = 0; i < relBatches.Count; i++)
        {
            var batch = relBatches[i];
            var label = $"relationships {i + 1}/{relBatches.Count} ({batch.Length} items)";
            await PostChunkAsync(
                new List<object>(),
                batch.ToList(),
                new Dictionary<string, string>(),
                chunkLabel: label);
        }

        // Phase 3: Final commit chunk — empty content, only file hashes (server marks lastIndexed)
        await PostChunkAsync(
            new List<object>(),
            new List<RelationshipDto>(),
            relativeHashes,
            chunkLabel: $"final commit ({relativeHashes.Count} file hashes)");

        _logger.LogInformation(
            "Chunked upload complete: {SymbolChunks} symbol chunks + {RelChunks} relationship chunks + 1 commit chunk",
            symbolBatches.Count, relBatches.Count);
    }

    private async Task PostChunkAsync(
        IReadOnlyList<object> symbols,
        IReadOnlyList<RelationshipDto> relationships,
        IReadOnlyDictionary<string, string> fileHashes,
        string chunkLabel)
    {
        var payload = new
        {
            projectName = _projectName,
            symbols,
            relationships,
            fileHashes
        };

        _logger.LogInformation("POST chunk [{Label}]...", chunkLabel);

        var url = $"{_serverUrl}/api/index/results";
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var response = await _httpClient.PostAsJsonAsync(url, payload, JsonOptions);
        sw.Stop();

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync();
            _logger.LogError("  → ERROR {Status}: {Error}", response.StatusCode, error);
            // Throw để abort chunked upload — partial state vẫn ở server, có thể resume
            throw new InvalidOperationException($"Chunk upload failed: {response.StatusCode} — {error}");
        }

        // HTTP 200 alone is not enough: the server will return 200 even when the
        // vector store silently dropped rows (historically hidden as WARN logs —
        // see issue #1). Parse the body and escalate any partial-persist failures
        // so the user's AI agent does not conclude "indexed" when it isn't.
        var body = await response.Content.ReadFromJsonAsync<UploadAck>(JsonOptions);
        if (body is null)
        {
            _logger.LogWarning("  → OK but response body missing; cannot verify persist counts");
            return;
        }

        if (body.EmbeddingsFailed > 0)
        {
            foreach (var w in body.Warnings ?? [])
                _logger.LogError("  → SERVER WARNING: {Warning}", w);

            _logger.LogError(
                "  → PARTIAL PERSIST: {Failed} of {Total} embedding rows failed for chunk [{Label}]. " +
                "Server logs have the stack trace. Aborting upload; queries will return stale / incomplete results.",
                body.EmbeddingsFailed, body.EmbeddingsPersisted + body.EmbeddingsFailed, chunkLabel);
            throw new InvalidOperationException(
                $"Server reported {body.EmbeddingsFailed} failed embedding upserts in chunk [{chunkLabel}]. " +
                "Do not query until the underlying issue is fixed and the chunk is re-uploaded.");
        }

        _logger.LogInformation(
            "  → OK ({Duration:F1}s) — persisted {Persisted} embeddings",
            sw.Elapsed.TotalSeconds, body.EmbeddingsPersisted);
    }

    /// <summary>
    /// Trimmed view of the server's IndexResultsResponse — only the fields the
    /// agent uses to decide success/failure. Avoids a cross-project dependency
    /// on CortexPlexus.App's DTOs.
    /// </summary>
    private sealed record UploadAck(
        int Symbols,
        int Relationships,
        int Embeddings,
        int EmbeddingsPersisted,
        int EmbeddingsFailed,
        IReadOnlyList<string>? Warnings,
        double DurationSeconds);

    /// <summary>
    /// Relationship DTO cho serialization. Dùng class thay vì anonymous type để
    /// có thể truyền giữa methods (chunking).
    /// </summary>
    private sealed class RelationshipDto
    {
        public string FromFqn { get; set; } = "";
        public string ToFqn { get; set; } = "";
        public string Type { get; set; } = "";
        public Dictionary<string, string>? Metadata { get; set; }
    }

    internal Dictionary<string, string> ComputeLocalHashes(string path)
    {
        var extensions = new[] { "*.cs", "*.ts", "*.tsx", "*.js", "*.jsx", "*.py", "*.md" };
        var excludedDirs = new[] { "bin", "obj", "node_modules", "__pycache__", ".venv", ".git", ".vs", ".idea" };

        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var ext in extensions)
        {
            foreach (var file in Directory.GetFiles(path, ext, SearchOption.AllDirectories))
            {
                var parts = file.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (parts.Any(p => excludedDirs.Contains(p, StringComparer.OrdinalIgnoreCase)))
                    continue;

                hashes[file] = ComputeFileHash(file);
            }
        }

        return hashes;
    }

    /// <summary>
    /// Test-friendly overload of FindChangedFiles cho phép inject server hashes mock.
    /// </summary>
    internal static List<string> FindChangedFiles(
        Dictionary<string, string> localHashes,
        Dictionary<string, string> serverHashes)
    {
        var changed = new List<string>();

        foreach (var (filePath, hash) in localHashes)
        {
            if (!serverHashes.TryGetValue(filePath, out var serverHash) || serverHash != hash)
            {
                changed.Add(filePath);
            }
        }

        return changed;
    }

    private List<string> FindChangedFiles(Dictionary<string, string> localHashes)
        => FindChangedFiles(localHashes, _serverHashes);

    internal static string ComputeFileHash(string filePath)
    {
        var bytes = File.ReadAllBytes(filePath);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }

    internal static IReadOnlyList<string> FindSolutionsAndProjects(string path)
    {
        if (path.EndsWith(".sln") || path.EndsWith(".slnx") || path.EndsWith(".csproj"))
            return File.Exists(path) ? [path] : [];

        if (!Directory.Exists(path)) return [];

        var excludedDirs = new[] { "bin", "obj", "node_modules" };

        var solutions = Directory.GetFiles(path, "*.sln", SearchOption.AllDirectories)
            .Concat(Directory.GetFiles(path, "*.slnx", SearchOption.AllDirectories))
            .Where(f => !IsExcludedPath(f, excludedDirs))
            .ToList();

        var allCsprojs = Directory.GetFiles(path, "*.csproj", SearchOption.AllDirectories)
            .Where(f => !IsExcludedPath(f, excludedDirs))
            .ToList();

        // No solution → every .csproj is a standalone unit.
        if (solutions.Count == 0)
            return allCsprojs;

        // R20 Issue #2 fix: some projects (commonly .Tests/) are not referenced by
        // any .sln but still contain code worth indexing. Discover .csproj files
        // that aren't mentioned in any solution file and add them alongside.
        var csprojInSolutions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var sln in solutions)
            csprojInSolutions.UnionWith(ExtractCsprojPathsFromSln(sln));

        var orphanCsprojs = allCsprojs
            .Where(csproj => !csprojInSolutions.Contains(Path.GetFullPath(csproj)))
            .ToList();

        return solutions.Concat(orphanCsprojs).ToList();
    }

    /// <summary>
    /// Extract absolute .csproj paths from a .sln file. Solution file format:
    /// <c>Project("{GUID}") = "ProjectName", "relative\path\to\Project.csproj", "{GUID}"</c>
    /// </summary>
    internal static IEnumerable<string> ExtractCsprojPathsFromSln(string slnPath)
    {
        if (!File.Exists(slnPath)) yield break;

        var slnDir = Path.GetDirectoryName(slnPath) ?? "";
        string[] lines;
        try { lines = File.ReadAllLines(slnPath); }
        catch (IOException) { yield break; }

        foreach (var line in lines)
        {
            if (!line.StartsWith("Project(", StringComparison.Ordinal)) continue;

            // Find the three quoted strings: "ProjectName", "relative\path.csproj", "{GUID}"
            var parts = line.Split('"');
            if (parts.Length < 6) continue;

            var relativePath = parts[5];
            if (!relativePath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                continue;

            // Normalize Windows-style backslashes that also work on Linux MSBuildWorkspace
            var normalized = relativePath.Replace('\\', Path.DirectorySeparatorChar);
            var absolute = Path.GetFullPath(Path.Combine(slnDir, normalized));
            yield return absolute;
        }
    }

    private static bool IsExcludedPath(string path, string[] excludedDirs)
    {
        var parts = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return parts.Any(p => excludedDirs.Contains(p, StringComparer.OrdinalIgnoreCase));
    }

    internal static bool IsTreeSitterFile(string path) =>
        path.EndsWith(".ts", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".tsx", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".js", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".jsx", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".py", StringComparison.OrdinalIgnoreCase);

    private string ToRelativePath(string absolutePath)
    {
        if (string.IsNullOrEmpty(_rootPath) || !absolutePath.StartsWith(_rootPath, StringComparison.OrdinalIgnoreCase))
            return absolutePath;

        return absolutePath[_rootPath.Length..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private object ToSymbolDto(CodeSymbol symbol)
    {
        var common = new Dictionary<string, object?>
        {
            ["fqn"] = symbol.Fqn,
            ["name"] = symbol.Name,
            ["kind"] = symbol.Kind,
            ["filePath"] = symbol.FilePath is not null ? ToRelativePath(symbol.FilePath) : null,
            ["startLine"] = symbol.StartLine,
            ["endLine"] = symbol.EndLine,
            ["documentation"] = symbol.Documentation,
            ["aiSummary"] = symbol.AiSummary
        };

        switch (symbol)
        {
            case ClassInfo c:
                common["accessibility"] = c.Accessibility;
                common["isAbstract"] = c.IsAbstract;
                common["isStatic"] = c.IsStatic;
                common["isSealed"] = c.IsSealed;
                common["isPartial"] = c.IsPartial;
                common["baseTypeFqn"] = c.BaseTypeFqn;
                common["interfaceFqns"] = c.InterfaceFqns;
                break;
            case MethodInfo m:
                common["signature"] = m.Signature;
                common["returnType"] = m.ReturnType;
                common["accessibility"] = m.Accessibility;
                common["isAsync"] = m.IsAsync;
                common["isStatic"] = m.IsStatic;
                common["isVirtual"] = m.IsVirtual;
                common["isOverride"] = m.IsOverride;
                common["isTestMethod"] = m.IsTestMethod;
                common["containingTypeFqn"] = m.ContainingTypeFqn;
                common["parameters"] = m.Parameters.Select(p => new { name = p.Name, type = p.Type, position = p.Position });
                break;
            case InterfaceInfo i:
                common["accessibility"] = i.Accessibility;
                common["memberFqns"] = i.MemberFqns;
                break;
            case PropertyInfo p:
                common["type"] = p.Type;
                common["hasGetter"] = p.HasGetter;
                common["hasSetter"] = p.HasSetter;
                common["containingTypeFqn"] = p.ContainingTypeFqn;
                break;
            case ConstructorInfo ct:
                common["signature"] = ct.Signature;
                common["accessibility"] = ct.Accessibility;
                common["containingTypeFqn"] = ct.ContainingTypeFqn;
                common["parameters"] = ct.Parameters.Select(p => new { name = p.Name, type = p.Type, position = p.Position });
                break;
            case DocumentSection d:
                common["level"] = d.Level;
                // Truncate content to first 500 chars — avoid sending full doc text
                common["content"] = d.Content.Length > 500 ? d.Content[..500] + "..." : d.Content;
                common["documentPath"] = d.DocumentPath;
                break;
            case DbContextInfo db:
                common["dbSets"] = db.DbSets.Select(s => new { entityTypeFqn = s.EntityTypeFqn, propertyName = s.PropertyName, tableName = s.TableName });
                break;
            case DiRegistrationInfo di:
                common["serviceTypeFqn"] = di.ServiceTypeFqn;
                common["implementationTypeFqn"] = di.ImplementationTypeFqn;
                common["lifetime"] = di.Lifetime;
                common["moduleName"] = di.ModuleName;
                break;
            case ApiEndpointInfo api:
                common["httpMethod"] = api.HttpMethod;
                common["routeTemplate"] = api.RouteTemplate;
                common["handlerMethodFqn"] = api.HandlerMethodFqn;
                common["endpointName"] = api.EndpointName;
                common["summary"] = api.Summary;
                common["moduleName"] = api.ModuleName;
                break;
        }

        return common;
    }
}
