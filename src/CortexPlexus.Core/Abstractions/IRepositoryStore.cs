using CortexPlexus.Core.Models;

namespace CortexPlexus.Core.Abstractions;

public interface IRepositoryStore
{
    Task<RepositoryInfo> RegisterAsync(string name, string path, CancellationToken ct = default);
    Task<RepositoryInfo?> GetByPathAsync(string path, CancellationToken ct = default);
    Task<IReadOnlyList<RepositoryInfo>> ListAsync(CancellationToken ct = default);
    Task UpdateLastIndexedAsync(Guid repoId, CancellationToken ct = default);
    Task<bool> IsFileChangedAsync(string filePath, Guid repoId, string contentHash, CancellationToken ct = default);
    Task UpdateFileHashAsync(string filePath, Guid repoId, string contentHash, CancellationToken ct = default);
    Task<Dictionary<string, string>> GetFileHashesAsync(Guid repoId, CancellationToken ct = default);
}
