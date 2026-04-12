using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using CortexPlexus.Core.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Retry;

namespace CortexPlexus.Embedding;

public sealed class GeminiEmbeddingService(
    IHttpClientFactory httpClientFactory,
    IOptions<EmbeddingOptions> options,
    ILogger<GeminiEmbeddingService> logger) : IEmbeddingService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly EmbeddingOptions _options = options.Value;

    private readonly ResiliencePipeline<HttpResponseMessage> _pipeline =
        new ResiliencePipelineBuilder<HttpResponseMessage>()
            .AddRetry(new RetryStrategyOptions<HttpResponseMessage>
            {
                MaxRetryAttempts = 3,
                BackoffType = DelayBackoffType.Exponential,
                Delay = TimeSpan.FromSeconds(1),
                ShouldHandle = new PredicateBuilder<HttpResponseMessage>()
                    .HandleResult(r =>
                        r.StatusCode is System.Net.HttpStatusCode.TooManyRequests
                            or System.Net.HttpStatusCode.RequestTimeout
                            or System.Net.HttpStatusCode.ServiceUnavailable
                            or System.Net.HttpStatusCode.InternalServerError)
                    .Handle<HttpRequestException>()
                    .Handle<TaskCanceledException>()
            })
            .Build();

    public async Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        try
        {
            var client = httpClientFactory.CreateClient(nameof(GeminiEmbeddingService));
            var url = $"https://generativelanguage.googleapis.com/v1beta/models/{_options.GeminiModel}:embedContent?key={_options.ApiKey}";

            var request = new GeminiEmbedRequest
            {
                Model = $"models/{_options.GeminiModel}",
                Content = new GeminiContent
                {
                    Parts = [new GeminiPart { Text = text }]
                },
                TaskType = _options.TaskType,
                OutputDimensionality = _options.Dimensions
            };

            var response = await _pipeline.ExecuteAsync(async token =>
            {
                var httpResponse = await client.PostAsJsonAsync(url, request, JsonOptions, token);
                return httpResponse;
            }, ct);

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                logger.LogWarning("Gemini API returned {StatusCode}: {Body}", response.StatusCode, body);
                return [];
            }

            var result = await response.Content.ReadFromJsonAsync<GeminiEmbedResponse>(JsonOptions, ct);
            return result?.Embedding?.Values ?? [];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to generate embedding via Gemini");
            return [];
        }
    }

    public async Task<IReadOnlyList<float[]>> EmbedBatchAsync(IEnumerable<string> texts, CancellationToken ct = default)
    {
        var textList = texts as IList<string> ?? texts.ToList();
        var results = new List<float[]>(textList.Count);

        foreach (var batch in Chunk(textList, _options.MaxBatchSize))
        {
            ct.ThrowIfCancellationRequested();
            var batchResults = await EmbedBatchChunkAsync(batch, ct);
            results.AddRange(batchResults);
        }

        return results;
    }

    private async Task<IReadOnlyList<float[]>> EmbedBatchChunkAsync(IList<string> texts, CancellationToken ct)
    {
        try
        {
            var client = httpClientFactory.CreateClient(nameof(GeminiEmbeddingService));
            var url = $"https://generativelanguage.googleapis.com/v1beta/models/{_options.GeminiModel}:batchEmbedContents?key={_options.ApiKey}";

            var request = new GeminiBatchRequest
            {
                Requests = texts.Select(text => new GeminiEmbedRequest
                {
                    Model = $"models/{_options.GeminiModel}",
                    Content = new GeminiContent { Parts = [new GeminiPart { Text = text }] },
                    TaskType = _options.TaskType,
                    OutputDimensionality = _options.Dimensions
                }).ToList()
            };

            logger.LogInformation("Batch embedding {Count} texts in 1 API call", texts.Count);

            var response = await _pipeline.ExecuteAsync(async token =>
            {
                var httpResponse = await client.PostAsJsonAsync(url, request, JsonOptions, token);
                return httpResponse;
            }, ct);

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                logger.LogWarning("Gemini batch API returned {StatusCode}: {Body}", response.StatusCode, body);
                return texts.Select(_ => Array.Empty<float>()).ToList();
            }

            var result = await response.Content.ReadFromJsonAsync<GeminiBatchResponse>(JsonOptions, ct);
            return result?.Embeddings?.Select(e => e.Values ?? []).ToList()
                ?? texts.Select(_ => Array.Empty<float>()).ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to batch embed {Count} texts via Gemini", texts.Count);
            return texts.Select(_ => Array.Empty<float>()).ToList();
        }
    }

    private static IEnumerable<IList<T>> Chunk<T>(IList<T> source, int size)
    {
        for (var i = 0; i < source.Count; i += size)
        {
            yield return source.Skip(i).Take(size).ToList();
        }
    }

    // Request / Response DTOs

    private sealed class GeminiEmbedRequest
    {
        public string Model { get; set; } = default!;
        public GeminiContent Content { get; set; } = default!;
        public string TaskType { get; set; } = default!;
        public int OutputDimensionality { get; set; }
    }

    private sealed class GeminiContent
    {
        public List<GeminiPart> Parts { get; set; } = [];
    }

    private sealed class GeminiPart
    {
        public string Text { get; set; } = default!;
    }

    private sealed class GeminiEmbedResponse
    {
        public GeminiEmbeddingResult? Embedding { get; set; }
    }

    private sealed class GeminiEmbeddingResult
    {
        public float[]? Values { get; set; }
    }

    // Batch DTOs
    private sealed class GeminiBatchRequest
    {
        public List<GeminiEmbedRequest> Requests { get; set; } = [];
    }

    private sealed class GeminiBatchResponse
    {
        public List<GeminiEmbeddingResult>? Embeddings { get; set; }
    }
}
