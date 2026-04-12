using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using CortexPlexus.Core.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CortexPlexus.Search.QueryExpansion;

/// <summary>
/// Query expander using Ollama local LLM for HyDE and multi-query generation.
/// Calls Ollama /api/generate endpoint (text generation, not embedding).
/// </summary>
public sealed class OllamaQueryExpander(
    IHttpClientFactory httpClientFactory,
    IOptions<QueryExpansionOptions> options,
    ILogger<OllamaQueryExpander> logger) : IQueryExpander
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly QueryExpansionOptions _options = options.Value;

    public bool IsEnabled => _options.Enabled;

    public async Task<string?> ExpandHydeAsync(string query, CancellationToken ct = default)
    {
        var prompt = $"""
            You are a code search assistant. Given a search query, write a short technical paragraph (3-5 sentences) that would appear in source code documentation or comments answering this query. Focus on class names, method signatures, and technical terms.

            Query: {query}

            Answer:
            """;

        var response = await GenerateAsync(prompt, ct);
        if (string.IsNullOrWhiteSpace(response))
        {
            logger.LogDebug("HyDE expansion returned empty for query: {Query}", query);
            return null;
        }

        logger.LogDebug("HyDE expanded '{Query}' → {Length} chars", query, response.Length);
        return response;
    }

    public async Task<IReadOnlyList<string>> ExpandMultiQueryAsync(string query, int variants = 3, CancellationToken ct = default)
    {
        var prompt = $"""
            You are a code search assistant. Given a search query, generate exactly {variants} alternative search queries that approach the same topic from different angles. Focus on different keywords, synonyms, and phrasings that might match source code.

            Original query: {query}

            Return ONLY the {variants} queries, one per line, without numbering or prefixes:
            """;

        var response = await GenerateAsync(prompt, ct);
        if (string.IsNullOrWhiteSpace(response))
        {
            logger.LogDebug("Multi-query expansion returned empty for query: {Query}", query);
            return [query];
        }

        var expanded = response
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => line.Length > 2 && !line.StartsWith('#'))
            .Select(line => line.TrimStart('-', '*', ' ', '\t'))
            .Where(line => line.Length > 2)
            .Take(variants)
            .ToList();

        if (expanded.Count == 0)
            return [query];

        // Always include original query
        if (!expanded.Contains(query, StringComparer.OrdinalIgnoreCase))
            expanded.Insert(0, query);

        logger.LogDebug("Multi-query expanded '{Query}' → {Count} variants", query, expanded.Count);
        return expanded;
    }

    private async Task<string?> GenerateAsync(string prompt, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));

            var client = httpClientFactory.CreateClient(nameof(OllamaQueryExpander));
            var url = $"{_options.OllamaBaseUrl.TrimEnd('/')}/api/generate";

            var request = new OllamaGenerateRequest
            {
                Model = _options.OllamaModel,
                Prompt = prompt,
                Stream = false,
                Options = new OllamaGenerateOptions
                {
                    Temperature = 0.7f,
                    NumPredict = 256
                }
            };

            var response = await client.PostAsJsonAsync(url, request, JsonOptions, cts.Token);

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cts.Token);
                logger.LogWarning("Ollama generate API returned {StatusCode}: {Body}", response.StatusCode, body);
                return null;
            }

            var result = await response.Content.ReadFromJsonAsync<OllamaGenerateResponse>(JsonOptions, cts.Token);
            return result?.Response?.Trim();
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("Ollama query expansion timed out after {Timeout}s", _options.TimeoutSeconds);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to expand query via Ollama");
            return null;
        }
    }

    // Ollama /api/generate DTOs

    private sealed class OllamaGenerateRequest
    {
        public string Model { get; set; } = default!;
        public string Prompt { get; set; } = default!;
        public bool Stream { get; set; }
        public OllamaGenerateOptions? Options { get; set; }
    }

    private sealed class OllamaGenerateOptions
    {
        public float Temperature { get; set; }

        [JsonPropertyName("num_predict")]
        public int NumPredict { get; set; }
    }

    private sealed class OllamaGenerateResponse
    {
        public string? Response { get; set; }
    }
}
