namespace CortexPlexus.Core.Models;

public sealed record ParseResult(
    IReadOnlyList<CodeSymbol> Symbols,
    IReadOnlyList<Relationship> Relationships,
    TimeSpan Duration,
    int FilesProcessed,
    int ErrorCount
);
