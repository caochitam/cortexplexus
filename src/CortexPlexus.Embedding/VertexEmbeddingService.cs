using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using CortexPlexus.Core.Abstractions;
using Google.Apis.Auth.OAuth2;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Retry;

namespace CortexPlexus.Embedding;

/// <summary>
/// Vertex AI embedding provider (ADR-017) — opt-in branch for the tri-cortex
/// deployment. Calls the Vertex <c>:predict</c> endpoint with API-key (express
/// mode) auth on the query string. Mirrors <see cref="GeminiEmbeddingService"/>'s
/// resilience + graceful-empty contract, but SUB-BATCHES <see cref="EmbedBatchAsync"/>
/// to <see cref="EmbeddingOptions.VertexInstancesPerCall"/> (Vertex caps instances
/// per call; <c>text-embedding-004/005</c> = 5), unlike Gemini's single 100-instance
/// batch call.
/// </summary>
public sealed class VertexEmbeddingService(
    IHttpClientFactory httpClientFactory,
    IOptions<EmbeddingOptions> options,
    ILogger<VertexEmbeddingService> logger) : IEmbeddingService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly EmbeddingOptions _options = options.Value;

    // Lazily-built SA credential (scoped, auto-refreshing) + the project id read from the
    // SA file. Guarded so concurrent embedding batches build them exactly once.
    private readonly SemaphoreSlim _credLock = new(1, 1);
    private GoogleCredential? _credential;
    private string? _saProjectId;

    private bool UsesServiceAccount => !string.IsNullOrWhiteSpace(_options.VertexServiceAccountJsonPath);

    private readonly ResiliencePipeline<HttpResponseMessage> _pipeline =
        new ResiliencePipelineBuilder<HttpResponseMessage>()
            .AddRetry(new RetryStrategyOptions<HttpResponseMessage>
            {
                // Rate limits (429) are common on constrained Vertex quotas — retry patiently
                // with jittered exponential backoff (2s,4s,8s,16s,32s) instead of dropping the
                // batch after a few quick attempts.
                MaxRetryAttempts = 5,
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                Delay = TimeSpan.FromSeconds(2),
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
        var batch = await EmbedSubBatchAsync([text], ct);
        return batch.Count > 0 ? batch[0] : [];
    }

    public async Task<IReadOnlyList<float[]>> EmbedBatchAsync(IEnumerable<string> texts, CancellationToken ct = default)
    {
        var textList = texts as IList<string> ?? texts.ToList();
        var results = new List<float[]>(textList.Count);

        // Vertex caps instances per :predict call (text-embedding-004/005 = 5).
        // Sub-batch to that cap and issue one call per sub-batch.
        var cap = _options.VertexInstancesPerCall > 0 ? _options.VertexInstancesPerCall : 5;
        foreach (var subBatch in Chunk(textList, cap))
        {
            ct.ThrowIfCancellationRequested();
            results.AddRange(await EmbedSubBatchAsync(subBatch, ct));
        }

        return results;
    }

    private async Task<IReadOnlyList<float[]>> EmbedSubBatchAsync(IList<string> texts, CancellationToken ct)
    {
        try
        {
            var client = httpClientFactory.CreateClient(nameof(VertexEmbeddingService));
            var url = await BuildPredictUrlAsync(ct);

            var request = new VertexPredictRequest
            {
                Instances = texts.Select(t => new VertexInstance { Content = t }).ToList(),
                Parameters = new VertexParameters { OutputDimensionality = _options.Dimensions }
            };

            // Bearer token is fetched once per sub-batch (GoogleCredential caches + refreshes);
            // the request message is rebuilt inside the retry lambda since an HttpRequestMessage
            // cannot be resent.
            var bearer = UsesServiceAccount ? await GetAccessTokenAsync(ct) : null;

            var response = await _pipeline.ExecuteAsync(async token =>
            {
                using var message = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = JsonContent.Create(request, options: JsonOptions)
                };
                if (bearer is not null)
                    message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
                return await client.SendAsync(message, token);
            }, ct);

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                logger.LogWarning("Vertex :predict returned {StatusCode}: {Body}", response.StatusCode, body);
                return texts.Select(_ => Array.Empty<float>()).ToList();
            }

            var result = await response.Content.ReadFromJsonAsync<VertexPredictResponse>(JsonOptions, ct);
            var predictions = result?.Predictions;

            // Map one embedding per input; pad with empty if the API returned fewer
            // predictions than instances (never drop/realign items).
            return texts.Select((_, i) =>
                predictions is not null && i < predictions.Count
                    ? predictions[i].Embeddings?.Values ?? []
                    : Array.Empty<float>()).ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to embed {Count} texts via Vertex", texts.Count);
            return texts.Select(_ => Array.Empty<float>()).ToList();
        }
    }

    private async Task<string> BuildPredictUrlAsync(CancellationToken ct)
    {
        var location = string.IsNullOrWhiteSpace(_options.VertexLocation) ? "global" : _options.VertexLocation;

        // location "global" targets the bare host with NO region prefix;
        // any region (e.g. us-central1) prefixes the host.
        var host = location.Equals("global", StringComparison.OrdinalIgnoreCase)
            ? "aiplatform.googleapis.com"
            : $"{location}-aiplatform.googleapis.com";

        var projectId = !string.IsNullOrWhiteSpace(_options.VertexProjectId)
            ? _options.VertexProjectId
            : await GetProjectIdAsync(ct);

        var baseUrl = $"https://{host}/v1/projects/{projectId}/locations/{location}" +
                      $"/publishers/google/models/{_options.VertexModelId}:predict";

        // Service-account path → OAuth Bearer (Authorization header), no query key.
        if (UsesServiceAccount)
            return baseUrl;

        // Express mode → API key on the query string.
        var apiKey = !string.IsNullOrWhiteSpace(_options.VertexApiKey) ? _options.VertexApiKey : _options.ApiKey;
        return $"{baseUrl}?key={apiKey}";
    }

    /// <summary>
    /// Mint a fresh (cached / auto-refreshed) OAuth2 access token from the configured
    /// service-account JSON, scoped to <c>cloud-platform</c>. Built once, then reused.
    /// </summary>
    private async Task<string?> GetAccessTokenAsync(CancellationToken ct)
    {
        var credential = await GetCredentialAsync(ct);
        return await ((ITokenAccess)credential).GetAccessTokenForRequestAsync(cancellationToken: ct);
    }

    private async Task<GoogleCredential> GetCredentialAsync(CancellationToken ct)
    {
        if (_credential is not null) return _credential;
        await _credLock.WaitAsync(ct);
        try
        {
            _credential ??= GoogleCredential
                .FromFile(_options.VertexServiceAccountJsonPath!)
                .CreateScoped("https://www.googleapis.com/auth/cloud-platform");
        }
        finally
        {
            _credLock.Release();
        }
        return _credential;
    }

    /// <summary>Project id read from the SA JSON's own <c>project_id</c> field (cached).</summary>
    private async Task<string?> GetProjectIdAsync(CancellationToken ct)
    {
        if (_saProjectId is not null) return _saProjectId;
        if (!UsesServiceAccount) return _options.VertexProjectId;

        await _credLock.WaitAsync(ct);
        try
        {
            if (_saProjectId is null)
            {
                await using var stream = File.OpenRead(_options.VertexServiceAccountJsonPath!);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
                _saProjectId = doc.RootElement.TryGetProperty("project_id", out var pid)
                    ? pid.GetString()
                    : null;
            }
        }
        finally
        {
            _credLock.Release();
        }
        return _saProjectId;
    }

    private static IEnumerable<IList<T>> Chunk<T>(IList<T> source, int size)
    {
        for (var i = 0; i < source.Count; i += size)
        {
            yield return source.Skip(i).Take(size).ToList();
        }
    }

    // Request / Response DTOs — Vertex :predict wire (ADR-017)

    private sealed class VertexPredictRequest
    {
        public List<VertexInstance> Instances { get; set; } = [];
        public VertexParameters Parameters { get; set; } = default!;
    }

    private sealed class VertexInstance
    {
        public string Content { get; set; } = default!;
    }

    private sealed class VertexParameters
    {
        public int OutputDimensionality { get; set; }
    }

    private sealed class VertexPredictResponse
    {
        public List<VertexPrediction>? Predictions { get; set; }
    }

    private sealed class VertexPrediction
    {
        public VertexEmbeddings? Embeddings { get; set; }
    }

    private sealed class VertexEmbeddings
    {
        public float[]? Values { get; set; }
    }
}
