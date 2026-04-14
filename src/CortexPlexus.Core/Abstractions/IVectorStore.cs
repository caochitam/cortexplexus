using CortexPlexus.Core.Models;

namespace CortexPlexus.Core.Abstractions;

/// <summary>
/// Summary of a vector-upsert call. Returned instead of plain Task so the
/// caller (and, transitively, the local agent and the AI client) can tell
/// whether every symbol actually landed in the vector store or whether the
/// implementation silently dropped some — e.g. pgvector type-cache misses
/// that used to surface only as server-side WARN logs (see issue #1).
/// </summary>
/// <param name="Persisted">Symbols whose row was written successfully.</param>
/// <param name="Failed">Symbols whose batch threw during write; each failure is logged with its exception at WARN level by the implementation.</param>
public readonly record struct VectorUpsertResult(int Persisted, int Failed)
{
    public int Total => Persisted + Failed;
    public bool HasFailures => Failed > 0;
    public static readonly VectorUpsertResult Empty = new(0, 0);
}

public interface IVectorStore
{
    Task<VectorUpsertResult> UpsertAsync(IEnumerable<CodeSymbol> symbols, IReadOnlyDictionary<string, float[]> embeddings, CancellationToken ct = default);
    Task<IReadOnlyList<SearchResult>> SearchAsync(float[] queryEmbedding, int limit = 10, Guid? repoId = null, string? kind = null, CancellationToken ct = default);
    Task DeleteByRepoAsync(Guid repoId, CancellationToken ct = default);
}
