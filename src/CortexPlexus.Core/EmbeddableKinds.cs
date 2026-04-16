namespace CortexPlexus.Core;

/// <summary>
/// Single source of truth for the symbol-kind allow-list that gets a vector
/// embedding written to <c>code_symbols.embedding</c>. Other kinds (field,
/// property, event, constructor, enum, parameter, namespace, …) still get
/// a row in the table but with <c>embedding IS NULL</c> — they are reachable
/// via graph traversal and full-text search but skip semantic search by
/// design (low signal, high cost).
///
/// Used by:
///  - <c>IndexingPipeline.IndexAsync</c> (server-side parse pipeline)
///  - <c>AgentApiEndpoints./api/index/results</c> (agent upload pipeline)
///  - <c>GraphTraversalTools.ListRepositories</c> (kind-aware Health metric, ADR 008)
///
/// User-facing spec: <c>docs/HEALTH-METRICS.md</c> and <c>docs/ARCHITECTURE.md §3.5</c>.
/// If you change this list, update both docs in the same PR.
/// </summary>
public static class EmbeddableKinds
{
    public static readonly string[] All =
    [
        "class",
        "method",
        "interface",
        "struct",
        "record",
        "function",
        "type",
        "document",
        "section",
    ];

    /// <summary>
    /// Postgres SQL literal — comma-separated, single-quoted — for use inside
    /// <c>WHERE kind IN (...)</c> and <c>FILTER (WHERE kind IN (...))</c>.
    /// Pre-baked once so callers don't string-build per query.
    /// </summary>
    public static readonly string SqlInClause = string.Join(',', All.Select(k => $"'{k}'"));

    public static bool Contains(string? kind)
        => kind is not null && Array.IndexOf(All, kind) >= 0;
}
