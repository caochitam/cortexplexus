using CortexPlexus.Core.Models;

namespace CortexPlexus.Core.Abstractions;

public interface IVectorStore
{
    Task UpsertAsync(IEnumerable<CodeSymbol> symbols, IReadOnlyDictionary<string, float[]> embeddings, CancellationToken ct = default);
    Task<IReadOnlyList<SearchResult>> SearchAsync(float[] queryEmbedding, int limit = 10, Guid? repoId = null, string? kind = null, CancellationToken ct = default);
    Task DeleteByRepoAsync(Guid repoId, CancellationToken ct = default);
}
