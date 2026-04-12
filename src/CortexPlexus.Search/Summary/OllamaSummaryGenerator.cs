using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using CortexPlexus.Core.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CortexPlexus.Search.Summary;

/// <summary>
/// Generates AI summaries using Ollama local LLM (/api/generate).
/// Each method gets a 1-2 sentence summary: "Validates order, calculates total, saves to DB."
/// </summary>
public sealed class OllamaSummaryGenerator(
    IHttpClientFactory httpClientFactory,
    IOptions<SummaryOptions> options,
    ILogger<OllamaSummaryGenerator> logger) : ISummaryGenerator
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly SummaryOptions _options = options.Value;

    public bool IsEnabled => _options.Enabled;

    public async Task<string?> SummarizeAsync(string signature, string? documentation, CancellationToken ct = default)
    {
        if (!IsEnabled) return null;

        var prompt = BuildPrompt(signature, documentation);
        return await GenerateAsync(prompt, ct);
    }

    public async Task<IReadOnlyList<string?>> SummarizeBatchAsync(
        IReadOnlyList<(string Signature, string? Documentation)> items, CancellationToken ct = default)
    {
        if (!IsEnabled)
            return new string?[items.Count];

        var results = new string?[items.Count];
        var semaphore = new SemaphoreSlim(_options.MaxConcurrency);

        var tasks = items.Select(async (item, index) =>
        {
            await semaphore.WaitAsync(ct);
            try
            {
                var prompt = BuildPrompt(item.Signature, item.Documentation);
                results[index] = await GenerateAsync(prompt, ct);
            }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(tasks);
        return results;
    }

    private static string BuildPrompt(string signature, string? documentation)
    {
        var docPart = documentation is not null ? $"\nDocumentation: {documentation}" : "";
        return $"""
            Summarize this code symbol in 1-2 short sentences. Focus on WHAT it does, not HOW.
            Be concise. No markdown, no bullet points, just plain text.

            Signature: {signature}{docPart}

            Summary:
            """;
    }

    private async Task<string?> GenerateAsync(string prompt, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));

            var client = httpClientFactory.CreateClient(nameof(OllamaSummaryGenerator));
            var url = $"{_options.OllamaBaseUrl.TrimEnd('/')}/api/generate";

            var request = new
            {
                model = _options.OllamaModel,
                prompt,
                stream = false,
                options = new { temperature = 0.3f, num_predict = 100 }
            };

            var response = await client.PostAsJsonAsync(url, request, JsonOptions, cts.Token);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogDebug("Ollama summary failed: {Status}", response.StatusCode);
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(cts.Token);
            using var doc = JsonDocument.Parse(json);

            if (doc.RootElement.TryGetProperty("response", out var responseText))
            {
                var summary = responseText.GetString()?.Trim();
                // Limit to ~200 chars to keep it concise
                if (summary is not null && summary.Length > 200)
                    summary = summary[..200].TrimEnd() + "...";
                return string.IsNullOrWhiteSpace(summary) ? null : summary;
            }

            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Summary generation failed");
            return null;
        }
    }
}
