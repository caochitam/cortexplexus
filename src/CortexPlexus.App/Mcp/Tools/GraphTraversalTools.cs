using System.ComponentModel;
using CortexPlexus.Core.Abstractions;
using CortexPlexus.Core.Models;
using CortexPlexus.Search;
using ModelContextProtocol.Server;
using Npgsql;

namespace CortexPlexus.App.Mcp.Tools;

[McpServerToolType]
public sealed class GraphTraversalTools
{
    // R21 Fix #2: params declared as nullable strings (default null) so the MCP
    // framework doesn't throw ArgumentException for missing-param cases. We
    // validate explicitly and return a friendly error string instead.
    private static string? RequireFqn(string? provided, string paramName, string alternative)
        => string.IsNullOrWhiteSpace(provided)
            ? $"Missing required parameter '{paramName}'. Pass the fully qualified name of the target symbol. {alternative}"
            : null;

    // R23 N3+N4+N6: shared filter for stdlib / framework / primitive symbols.
    // Used by tools that traverse the call graph and dependency graph (get_callees,
    // get_callers, get_dependencies, get_data_flow, get_impact_analysis) to suppress
    // noise from System.*, Microsoft.*, third-party SDKs, and CLR primitives.
    public static readonly string[] FrameworkPrefixes =
    {
        "System.",
        "Microsoft.",
        "StackExchange.",
        "Npgsql.",
        "Polly.",
        "MediatR.",
        "Serilog.",
        "Newtonsoft.",
        "Qdrant.",
    };

    // CLR primitive aliases that show up in dependency results as their literal name
    // (no namespace prefix). These are language keywords, not "dependencies" worth
    // listing in any analysis.
    public static readonly HashSet<string> ClrPrimitives = new(StringComparer.Ordinal)
    {
        "string", "int", "long", "short", "byte", "sbyte", "uint", "ulong", "ushort",
        "float", "double", "decimal", "bool", "char", "object", "void",
        "string?", "int?", "long?", "bool?", "double?", "decimal?",
        "DateTime", "Guid", "TimeSpan", "DateTimeOffset",
    };

    public static bool IsExternalOrPrimitive(string fqn)
    {
        if (string.IsNullOrEmpty(fqn)) return false;
        if (ClrPrimitives.Contains(fqn)) return true;
        foreach (var prefix in FrameworkPrefixes)
        {
            if (fqn.StartsWith(prefix, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    public static IReadOnlyList<SearchResult> StripFrameworkNoise(
        IReadOnlyList<SearchResult> results)
        => results.Where(r => !IsExternalOrPrimitive(r.Fqn)).ToList();

    /// <summary>
    /// R22 Fix #3: When a tool that expects a method FQN receives what looks like a
    /// class FQN (no parens, kind != method/constructor) and returns no results,
    /// build a "did you mean" hint listing actual methods of that class. Returns
    /// <c>null</c> if no methods are found (caller falls back to plain "no results").
    /// </summary>
    private static async Task<string?> BuildClassFqnHintAsync(
        IGraphStore graphStore, string fqn, string toolName)
    {
        // Heuristic: if the FQN already contains '(' it was a method signature.
        // Don't bother running the lookup.
        if (fqn.Contains('(')) return null;

        // Strategy 1: treat the FQN as a class and list its methods.
        // Common case: user passed `Namespace.Class` instead of a method FQN.
        var methods = await graphStore.LookupMethodsByContainingTypeAsync(fqn, limit: 10);
        var hintTarget = fqn;

        // R25 R24-5 fix: strategy 2 — if strategy 1 found nothing, the FQN might
        // be a typo'd method name like "Namespace.Class.AnalyzeAsync" (real:
        // DirectAsync). Walk up one segment and try the parent as a class FQN.
        // This catches the common AI-agent failure mode of guessing a wrong
        // method name on a real class.
        if (methods.Count == 0)
        {
            var lastDot = fqn.LastIndexOf('.');
            if (lastDot > 0)
            {
                var parentFqn = fqn[..lastDot];
                methods = await graphStore.LookupMethodsByContainingTypeAsync(parentFqn, limit: 10);
                hintTarget = parentFqn;
            }
        }

        if (methods.Count == 0) return null;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"No results for '{fqn}'.");
        sb.AppendLine();
        if (hintTarget != fqn)
        {
            sb.AppendLine($"Hint: '{fqn}' may be a typo. {toolName} found no method by that name " +
                          $"on '{hintTarget.Split('.').LastOrDefault() ?? hintTarget}'.");
            sb.AppendLine($"Did you mean one of these methods?");
        }
        else
        {
            sb.AppendLine($"Hint: '{fqn}' looks like a class FQN. {toolName} expects a method FQN.");
            sb.AppendLine($"Try one of these methods of '{fqn.Split('.').LastOrDefault() ?? fqn}':");
        }
        foreach (var m in methods)
        {
            sb.AppendLine($"  {m.Fqn}");
        }
        if (methods.Count == 10)
            sb.AppendLine($"  ... (more methods exist; this is the first 10 alphabetically)");
        return sb.ToString();
    }

    [McpServerTool, Description("Find all methods that call a specified method")]
    public static async Task<string> GetCallers(
        [Description("Fully qualified method name (e.g. 'Namespace.Class.Method')")] string? methodFqn = null,
        [Description("Depth of call chain to traverse (1-5)")] int depth = 1,
        IGraphStore graphStore = default!,
        ContextCompressor compressor = default!)
    {
        var err = RequireFqn(methodFqn, "methodFqn",
            "Example: GetCallers(methodFqn: 'CortexFlow.Infrastructure.Services.ChatOrchestrator.ProcessAsync')");
        if (err is not null) return err;

        depth = Math.Clamp(depth, 1, 5);
        var results = await graphStore.QueryCallersAsync(methodFqn!, depth);
        if (results.Count > 0)
            return compressor.Compress(results);

        // R22 Fix #3: try to provide a hint if user passed a class FQN
        var hint = await BuildClassFqnHintAsync(graphStore, methodFqn!, "GetCallers");
        return hint ?? $"No callers found for '{methodFqn}'.";
    }

    [McpServerTool, Description("Find all methods called by a specified method")]
    public static async Task<string> GetCallees(
        [Description("Fully qualified method name (e.g. 'Namespace.Class.Method')")] string? methodFqn = null,
        [Description("Depth of call chain to traverse (1-5)")] int depth = 1,
        IGraphStore graphStore = default!,
        ContextCompressor compressor = default!)
    {
        var err = RequireFqn(methodFqn, "methodFqn",
            "Example: GetCallees(methodFqn: 'CortexFlow.Infrastructure.Services.ChatOrchestrator.ProcessAsync')");
        if (err is not null) return err;

        depth = Math.Clamp(depth, 1, 5);
        var rawResults = await graphStore.QueryCalleesAsync(methodFqn!, depth);
        // R23 N3+N4: drop System.*/Microsoft.*/CLR primitives — they dominate output
        // at depth>=2 and are never the answer to "what does this method call internally".
        var results = StripFrameworkNoise(rawResults);
        if (results.Count > 0)
            return compressor.Compress(results);

        // R22 Fix #3: same hint for callees
        var hint = await BuildClassFqnHintAsync(graphStore, methodFqn!, "GetCallees");
        return hint ?? $"No callees found for '{methodFqn}'.";
    }

    [McpServerTool, Description("Get dependencies of a class or method")]
    public static async Task<string> GetDependencies(
        [Description("Fully qualified name of class or method")] string? fqn = null,
        [Description("Depth of dependency chain (1-3)")] int depth = 1,
        IGraphStore graphStore = default!,
        ContextCompressor compressor = default!)
    {
        var err = RequireFqn(fqn, "fqn",
            "Example: GetDependencies(fqn: 'CortexFlow.API.Controllers.ChatController')");
        if (err is not null) return err;

        depth = Math.Clamp(depth, 1, 3);
        var rawResults = await graphStore.QueryDependenciesAsync(fqn!, depth);
        // R23 N3+N6: filter framework + CLR primitive types so user sees their own
        // domain dependencies, not 50 lines of System.Collections.Generic.List<T>.
        var results = StripFrameworkNoise(rawResults);
        return results.Count == 0
            ? $"No dependencies found for '{fqn}'."
            : compressor.Compress(results);
    }

    [McpServerTool, Description("Find all implementations of an interface")]
    public static async Task<string> GetImplementations(
        [Description("Fully qualified interface name (e.g. 'App.Interfaces.IFoo')")] string? interfaceFqn = null,
        IGraphStore graphStore = default!,
        ContextCompressor compressor = default!)
    {
        var err = RequireFqn(interfaceFqn, "interfaceFqn",
            "Example: GetImplementations(interfaceFqn: 'CortexFlow.Core.Interfaces.IChatOrchestrator')");
        if (err is not null) return err;

        var results = await graphStore.QueryImplementationsAsync(interfaceFqn!);
        return results.Count == 0
            ? $"No implementations found for '{interfaceFqn}'."
            : compressor.Compress(results);
    }

    [McpServerTool, Description("Get the inheritance hierarchy of a class")]
    public static async Task<string> GetClassHierarchy(
        [Description("Fully qualified class name")] string? classFqn = null,
        IGraphStore graphStore = default!,
        ContextCompressor compressor = default!)
    {
        var err = RequireFqn(classFqn, "classFqn",
            "Example: GetClassHierarchy(classFqn: 'CortexFlow.API.Controllers.ChatController')");
        if (err is not null) return err;

        var rawResults = await graphStore.QueryClassHierarchyAsync(classFqn!);

        // R23: use shared framework filter (was duplicated inline before).
        var results = StripFrameworkNoise(rawResults)
            .Where(r => !string.IsNullOrWhiteSpace(r.FilePath))
            .ToList();

        return results.Count == 0
            ? $"No hierarchy found for '{classFqn}'."
            : compressor.Compress(results);
    }

    [McpServerTool, Description("Analyze blast radius: find all code affected if a method or class changes")]
    public static async Task<string> GetImpactAnalysis(
        [Description("Fully qualified name of method or class to analyze")] string? fqn = null,
        [Description("Depth of impact chain (1-5)")] int depth = 2,
        IGraphStore graphStore = default!,
        ContextCompressor compressor = default!)
    {
        var err = RequireFqn(fqn, "fqn",
            "Example: GetImpactAnalysis(fqn: 'CortexFlow.Infrastructure.Services.ChatOrchestrator')");
        if (err is not null) return err;

        depth = Math.Clamp(depth, 1, 5);

        var callers = await graphStore.QueryCallersAsync(fqn!, depth);
        var implementations = await graphStore.QueryImplementationsAsync(fqn!);
        var hierarchy = await graphStore.QueryClassHierarchyAsync(fqn!);

        // Also find code that References this type (property access, field usage)
        var referencedBy = await graphStore.QueryReferencedByAsync(fqn!, depth);

        var allAffected = callers
            .Concat(implementations)
            .Concat(hierarchy)
            .Concat(referencedBy)
            .GroupBy(r => r.Fqn)
            .Select(g => g.First())
            .ToList();

        if (allAffected.Count == 0)
            return $"No impact found for '{fqn}'. It may not be referenced by other code.";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Impact analysis for: {fqn}");
        sb.AppendLine($"Total affected symbols: {allAffected.Count}");
        sb.AppendLine();

        if (callers.Count > 0)
        {
            sb.AppendLine($"--- Callers ({callers.Count}) ---");
            sb.AppendLine(compressor.Compress(callers));
        }
        if (referencedBy.Count > 0)
        {
            sb.AppendLine($"--- Referenced By ({referencedBy.Count}) ---");
            sb.AppendLine(compressor.Compress(referencedBy));
        }
        if (implementations.Count > 0)
        {
            sb.AppendLine($"--- Implementations ({implementations.Count}) ---");
            sb.AppendLine(compressor.Compress(implementations));
        }
        if (hierarchy.Count > 0)
        {
            sb.AppendLine($"--- Class Hierarchy ({hierarchy.Count}) ---");
            sb.AppendLine(compressor.Compress(hierarchy));
        }

        return sb.ToString();
    }

    [McpServerTool, Description(
        "Get test coverage for a method: which test methods exercise this production code. " +
        "Returns test methods linked via TestCovers edges (detected from [Fact], @Test, test_ prefix, etc.).")]
    public static async Task<string> GetTestCoverage(
        [Description("Fully qualified method name of the production code")] string? methodFqn = null,
        IGraphStore graphStore = default!,
        ContextCompressor compressor = default!)
    {
        var err = RequireFqn(methodFqn, "methodFqn",
            "Example: GetTestCoverage(methodFqn: 'CortexFlow.Infrastructure.Services.ChatOrchestrator.ProcessAsync')");
        if (err is not null) return err;

        var results = await graphStore.QueryTestCoverageAsync(methodFqn!);
        if (results.Count > 0)
            return $"Tests covering '{methodFqn}':\n\n{compressor.Compress(results)}";

        // R22 Fix #3: hint when user passes a class FQN
        var hint = await BuildClassFqnHintAsync(graphStore, methodFqn!, "GetTestCoverage");
        return hint ?? $"No tests found covering '{methodFqn}'. The method may not have tests, or test detection may not apply to its language.";
    }

    [McpServerTool, Description(
        "Find potentially dead code: public/internal methods with no callers. " +
        "Returns methods that are never called by other code — candidates for cleanup.")]
    public static async Task<string> GetDeadCode(
        [Description("Repository name to scan for dead code")] string repository,
        IGraphStore graphStore = default!,
        ContextCompressor compressor = default!,
        IRepositoryStore repoStore = default!)
    {
        var repoId = await RepoResolver.ResolveAsync(repository, repoStore);
        if (repoId is null)
            return $"Repository '{repository}' not found. Use ListRepositories to see available repos.";

        var results = await graphStore.QueryDeadCodeAsync(repoId.Value);
        if (results.Count == 0)
            return $"No dead code found in '{repository}'. All public/internal methods have callers.";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Potentially dead code in '{repository}' ({results.Count} methods):");
        sb.AppendLine("These public/internal methods have no callers in the codebase:");
        sb.AppendLine();
        sb.AppendLine(compressor.Compress(results));
        sb.AppendLine();
        sb.AppendLine("Note: Entry points, event handlers, and reflection-called methods may appear as false positives.");
        return sb.ToString();
    }

    [McpServerTool, Description("List all indexed repositories with their names, last-indexed timestamp, and persistence health (symbol count + embedding coverage). Check the 'health' line before assuming a repo is queryable — a registered repo with 0 symbols or missing embeddings means the last indexing run did not fully commit.")]
    public static async Task<string> ListRepositories(
        IRepositoryStore repoStore = default!,
        NpgsqlDataSource dataSource = default!)
    {
        var repos = await repoStore.ListAsync();
        if (repos.Count == 0)
            return "No repositories indexed yet.";

        // DB health check: per-repo symbol count + embedding coverage.
        // Lets the AI agent see "registered but empty" repos (issue #1 symptom)
        // without needing a separate tool call.
        var healthByRepo = new Dictionary<Guid, (long Symbols, long WithEmbedding)>();
        await using (var conn = await dataSource.OpenConnectionAsync())
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT repo_id, COUNT(*), COUNT(*) FILTER (WHERE embedding IS NOT NULL)
                FROM code_symbols
                GROUP BY repo_id
                """;
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                healthByRepo[reader.GetGuid(0)] = (reader.GetInt64(1), reader.GetInt64(2));
            }
        }

        // R21 Fix #1: when the same project has been indexed multiple times (e.g.
        // once via /workspace server path, once via agent at _agent/<name>), we end
        // up with multiple rows for the same logical repo. Group by Name and keep
        // the most recently indexed row; mention stale duplicates as a footnote so
        // users notice and can clean them up manually.
        var groups = repos
            .GroupBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var ordered = g.OrderByDescending(r => r.LastIndexed ?? DateTimeOffset.MinValue).ToList();
                return new { Name = g.Key, Primary = ordered[0], Stale = ordered.Skip(1).ToList() };
            })
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Indexed repositories:");
        sb.AppendLine();
        foreach (var g in groups)
        {
            var r = g.Primary;
            sb.AppendLine($"  Name: {r.Name}");
            sb.AppendLine($"  Path: {r.Path}");
            sb.AppendLine($"  Last indexed: {r.LastIndexed?.ToString("yyyy-MM-dd HH:mm:ss") ?? "never"}");

            if (healthByRepo.TryGetValue(r.Id, out var h))
            {
                var coverage = h.Symbols > 0 ? (double)h.WithEmbedding / h.Symbols : 0;
                var status = h.Symbols switch
                {
                    0 => "EMPTY — registered but no symbols persisted. Re-run indexing.",
                    _ when h.WithEmbedding == 0 => "DEGRADED — symbols present but no embeddings. Semantic search will fail; check server logs for vector-upsert warnings.",
                    _ when coverage < 0.9 => $"PARTIAL — {h.WithEmbedding}/{h.Symbols} ({coverage:P0}) symbols embedded. Some semantic hits will be missing.",
                    _ => $"OK — {h.Symbols} symbols, {h.WithEmbedding} with embeddings ({coverage:P0})"
                };
                sb.AppendLine($"  Health: {status}");
            }
            else
            {
                sb.AppendLine("  Health: UNKNOWN — no rows in code_symbols for this repo.");
            }

            if (g.Stale.Count > 0)
            {
                var stalePaths = string.Join(", ", g.Stale.Select(s => s.Path));
                sb.AppendLine($"  Note: {g.Stale.Count} stale duplicate(s) hidden: {stalePaths}");
            }
            sb.AppendLine();
        }

        sb.AppendLine("Tip: Use the 'repository' parameter in SearchCode/SemanticSearch/GetDiRegistrations/GetApiEndpoints to scope results to a specific project.");
        return sb.ToString();
    }
}
