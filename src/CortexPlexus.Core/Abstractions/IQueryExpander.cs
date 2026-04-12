namespace CortexPlexus.Core.Abstractions;

/// <summary>
/// Expands a user query into one or more improved search queries using LLM-based techniques
/// (HyDE, multi-query expansion, step-back prompting).
/// </summary>
public interface IQueryExpander
{
    /// <summary>
    /// Generates a hypothetical document/answer for HyDE-based vector search.
    /// Returns null if expansion is unavailable or fails.
    /// </summary>
    Task<string?> ExpandHydeAsync(string query, CancellationToken ct = default);

    /// <summary>
    /// Generates multiple query variants for broader retrieval coverage.
    /// Returns the original query if expansion is unavailable or fails.
    /// </summary>
    Task<IReadOnlyList<string>> ExpandMultiQueryAsync(string query, int variants = 3, CancellationToken ct = default);

    /// <summary>Whether this expander is active and can perform expansions.</summary>
    bool IsEnabled { get; }
}
