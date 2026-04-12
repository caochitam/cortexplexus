namespace CortexPlexus.Core.Models;

public sealed record SearchResult(
    string Fqn,
    string Name,
    string Kind,
    string? Signature,
    string? FilePath,
    int? StartLine,
    double Score,
    string Source,
    string? Documentation = null,
    string? AiSummary = null
);

public sealed record SearchRequest(
    string Query,
    SearchType Type = SearchType.Hybrid,
    int Limit = 20,
    Guid? RepoId = null,
    string? Kind = null,
    bool Expand = false
);

public enum SearchType { Graph, Vector, Bm25, Hybrid }
