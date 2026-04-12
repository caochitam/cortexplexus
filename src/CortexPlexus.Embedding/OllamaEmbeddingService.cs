using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using CortexPlexus.Core.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CortexPlexus.Embedding;

public sealed class OllamaEmbeddingService(
    IHttpClientFactory httpClientFactory,
    IOptions<EmbeddingOptions> options,
    ILogger<OllamaEmbeddingService> logger) : IEmbeddingService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly EmbeddingOptions _options = options.Value;

    public async Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        try
        {
            var client = httpClientFactory.CreateClient(nameof(OllamaEmbeddingService));
            var url = $"{_options.OllamaBaseUrl.TrimEnd('/')}/api/embed";

            var request = new OllamaEmbedRequest
            {
                Model = _options.OllamaModel,
                Input = text
            };

            var response = await client.PostAsJsonAsync(url, request, JsonOptions, ct);

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                logger.LogWarning("Ollama API returned {StatusCode}: {Body}", response.StatusCode, body);
                return [];
            }

            var result = await response.Content.ReadFromJsonAsync<OllamaEmbedResponse>(JsonOptions, ct);
            return result?.Embeddings is [{ } first, ..] ? first : [];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to generate embedding via Ollama");
            return [];
        }
    }

    public async Task<IReadOnlyList<float[]>> EmbedBatchAsync(IEnumerable<string> texts, CancellationToken ct = default)
    {
        try
        {
            var textList = texts as IList<string> ?? texts.ToList();
            var client = httpClientFactory.CreateClient(nameof(OllamaEmbeddingService));
            var url = $"{_options.OllamaBaseUrl.TrimEnd('/')}/api/embed";

            var request = new OllamaEmbedBatchRequest
            {
                Model = _options.OllamaModel,
                Input = textList.ToList()
            };

            var response = await client.PostAsJsonAsync(url, request, JsonOptions, ct);

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                logger.LogWarning("Ollama batch API returned {StatusCode}: {Body}", response.StatusCode, body);
                return textList.Select(_ => Array.Empty<float>()).ToArray();
            }

            var result = await response.Content.ReadFromJsonAsync<OllamaEmbedResponse>(JsonOptions, ct);

            if (result?.Embeddings is null)
                return textList.Select(_ => Array.Empty<float>()).ToArray();

            return result.Embeddings.Select(e => e ?? []).ToArray();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to generate batch embeddings via Ollama");
            var count = texts is ICollection<string> c ? c.Count : texts.Count();
            return Enumerable.Range(0, count).Select(_ => Array.Empty<float>()).ToArray();
        }
    }

    // Request / Response DTOs

    private sealed class OllamaEmbedRequest
    {
        public string Model { get; set; } = default!;
        public string Input { get; set; } = default!;
    }

    private sealed class OllamaEmbedBatchRequest
    {
        public string Model { get; set; } = default!;
        public List<string> Input { get; set; } = [];
    }

    private sealed class OllamaEmbedResponse
    {
        public List<float[]?>? Embeddings { get; set; }
    }
}
