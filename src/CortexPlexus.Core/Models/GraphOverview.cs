namespace CortexPlexus.Core.Models;

public sealed record GraphOverview(
    IReadOnlyList<GraphNode> Nodes,
    IReadOnlyList<GraphEdge> Edges,
    int TotalMatchingNodes = 0
);

public sealed record GraphNode(
    string Fqn,
    string Name,
    string Kind,
    string? Signature,
    string? FilePath,
    int? StartLine
);

public sealed record GraphEdge(
    string FromFqn,
    string ToFqn,
    string Type
);
