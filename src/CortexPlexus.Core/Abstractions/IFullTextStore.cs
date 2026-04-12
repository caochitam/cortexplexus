using CortexPlexus.Core.Models;

namespace CortexPlexus.Core.Abstractions;

public interface IFullTextStore
{
    Task<IReadOnlyList<SearchResult>> SearchAsync(string query, int limit = 10, Guid? repoId = null, string? kind = null, CancellationToken ct = default);
}
