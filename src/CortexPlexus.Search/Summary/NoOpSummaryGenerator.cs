using CortexPlexus.Core.Abstractions;

namespace CortexPlexus.Search.Summary;

/// <summary>
/// Pass-through implementation when summary generation is disabled.
/// </summary>
public sealed class NoOpSummaryGenerator : ISummaryGenerator
{
    public bool IsEnabled => false;

    public Task<string?> SummarizeAsync(string signature, string? documentation, CancellationToken ct = default)
        => Task.FromResult<string?>(null);

    public Task<IReadOnlyList<string?>> SummarizeBatchAsync(IReadOnlyList<(string Signature, string? Documentation)> items, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<string?>>(new string?[items.Count]);
}
