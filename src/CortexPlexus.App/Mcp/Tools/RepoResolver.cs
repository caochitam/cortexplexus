using CortexPlexus.Core.Abstractions;

namespace CortexPlexus.App.Mcp.Tools;

/// <summary>
/// Resolves repository name to Guid. Supports exact name match or partial match.
/// Returns null if no repository parameter provided (search all repos).
///
/// R25 R24-2 fix: when multiple rows match (same project indexed twice via
/// different paths — e.g. one via /workspace and one via _agent/&lt;name&gt;),
/// prefer the most recently indexed row. Without this guard,
/// <c>FirstOrDefault</c> picks whichever row LINQ enumerates first
/// (non-deterministic), and a stale historical index can hijack queries away
/// from fresh data — silently returning empty/wrong results.
/// </summary>
public static class RepoResolver
{
    public static async Task<Guid?> ResolveAsync(string? repository, IRepositoryStore repoStore, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(repository))
            return null;

        var repos = await repoStore.ListAsync(ct);

        // Exact match first — sorted by most-recent first to break ties.
        var exactMatches = repos
            .Where(r => r.Name.Equals(repository, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(r => r.LastIndexed ?? DateTimeOffset.MinValue)
            .ToList();
        if (exactMatches.Count > 0) return exactMatches[0].Id;

        // Partial match (contains) — same tie-break.
        var partialMatches = repos
            .Where(r => r.Name.Contains(repository, StringComparison.OrdinalIgnoreCase) ||
                        r.Path.Contains(repository, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(r => r.LastIndexed ?? DateTimeOffset.MinValue)
            .ToList();
        return partialMatches.Count > 0 ? partialMatches[0].Id : (Guid?)null;
    }
}
