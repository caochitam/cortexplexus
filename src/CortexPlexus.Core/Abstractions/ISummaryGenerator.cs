namespace CortexPlexus.Core.Abstractions;

/// <summary>
/// Generates AI summaries for code symbols (1-2 sentences describing what the symbol does).
/// Implementations: OllamaSummaryGenerator (local LLM), extensible to other providers.
/// </summary>
public interface ISummaryGenerator
{
    bool IsEnabled { get; }
    Task<string?> SummarizeAsync(string signature, string? documentation, CancellationToken ct = default);
    Task<IReadOnlyList<string?>> SummarizeBatchAsync(IReadOnlyList<(string Signature, string? Documentation)> items, CancellationToken ct = default);
}
