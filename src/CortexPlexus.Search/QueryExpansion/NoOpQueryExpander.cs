using CortexPlexus.Core.Abstractions;

namespace CortexPlexus.Search.QueryExpansion;

/// <summary>
/// Pass-through expander that returns the original query unchanged.
/// Used when query expansion is disabled or no LLM provider is available.
/// </summary>
public sealed class NoOpQueryExpander : IQueryExpander
{
    public bool IsEnabled => false;

    public Task<string?> ExpandHydeAsync(string query, CancellationToken ct = default)
        => Task.FromResult<string?>(null);

    public Task<IReadOnlyList<string>> ExpandMultiQueryAsync(string query, int variants = 3, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<string>>([query]);
}
