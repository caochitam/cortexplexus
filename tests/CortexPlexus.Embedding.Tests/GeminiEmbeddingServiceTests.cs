using System.Net;
using System.Text;
using CortexPlexus.Embedding;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CortexPlexus.Embedding.Tests;

/// <summary>
/// Tests cho GeminiEmbeddingService với HTTP mock.
///
/// Phạm vi: TEST-PLAN.md #72, #73, #74, #75, #76, #77, #78, #79
///
/// Strategy: dùng FakeHttpMessageHandler để mock Gemini REST API responses
/// — không gọi API thật.
/// </summary>
public class GeminiEmbeddingServiceTests
{
    private static GeminiEmbeddingService BuildService(
        FakeHttpMessageHandler handler,
        EmbeddingOptions? options = null)
    {
        var factory = new FakeHttpClientFactory(handler);
        var opts = Options.Create(options ?? new EmbeddingOptions
        {
            Provider = "gemini",
            ApiKey = "test-key",
            Dimensions = 768,
            MaxBatchSize = 100
        });
        return new GeminiEmbeddingService(factory, opts, NullLogger<GeminiEmbeddingService>.Instance);
    }

    // === #72: EmbedAsync_Success_ReturnsFloatArray ===
    [Fact]
    public async Task EmbedAsync_Success_ReturnsEmbeddingValues()
    {
        // Mục đích: Happy path — response 200 với embedding → trả float[].
        var json = """{"embedding":{"values":[0.1,0.2,0.3]}}""";
        var handler = FakeHttpMessageHandler.Ok(json);
        var service = BuildService(handler);

        var result = await service.EmbedAsync("test text");

        Assert.Equal(3, result.Length);
        Assert.Equal(0.1f, result[0]);
        Assert.Equal(0.2f, result[1]);
        Assert.Equal(0.3f, result[2]);
        Assert.Equal(1, handler.CallCount);
    }

    // === #73: EmbedAsync_429TooManyRequests_Retries3x ===
    [Fact]
    public async Task EmbedAsync_429RateLimit_RetriesAndEventuallyReturnsEmpty()
    {
        // Mục đích: 429 → retry policy kích hoạt (3 attempts), cuối cùng trả empty
        // nếu vẫn fail. Polly config trong GeminiEmbeddingService: MaxRetryAttempts=3
        // → tổng cộng 4 calls (1 original + 3 retries).
        var handler = FakeHttpMessageHandler.Error(HttpStatusCode.TooManyRequests, "rate limited");
        var service = BuildService(handler);

        var result = await service.EmbedAsync("test");

        Assert.Empty(result);
        // 1 + 3 retries = 4 calls total. Nếu handler count < 4, retry policy đã bị disable.
        Assert.Equal(4, handler.CallCount);
    }

    // === #74: EmbedAsync_500ServerError_Retries ===
    [Fact]
    public async Task EmbedAsync_500InternalError_RetriesBeforeGivingUp()
    {
        var handler = FakeHttpMessageHandler.Error(HttpStatusCode.InternalServerError, "server error");
        var service = BuildService(handler);

        var result = await service.EmbedAsync("test");

        Assert.Empty(result);
        Assert.Equal(4, handler.CallCount); // 1 + 3 retries
    }

    [Fact]
    public async Task EmbedAsync_ServiceUnavailable_Retries()
    {
        var handler = FakeHttpMessageHandler.Error(HttpStatusCode.ServiceUnavailable, "unavailable");
        var service = BuildService(handler);

        var result = await service.EmbedAsync("test");

        Assert.Empty(result);
        Assert.Equal(4, handler.CallCount);
    }

    // === #75: EmbedAsync_InvalidApiKey_ReturnsEmpty (no retry) ===
    [Fact]
    public async Task EmbedAsync_401Unauthorized_ReturnsEmptyNoRetry()
    {
        // Mục đích: 401 không trong retry list → trả empty ngay, không retry.
        var handler = FakeHttpMessageHandler.Error(HttpStatusCode.Unauthorized, "bad key");
        var service = BuildService(handler);

        var result = await service.EmbedAsync("test");

        Assert.Empty(result);
        Assert.Equal(1, handler.CallCount); // chỉ 1 call, không retry
    }

    // === #76: EmbedBatch_SmallBatch_SingleApiCall ===
    [Fact]
    public async Task EmbedBatchAsync_UnderBatchSize_SingleApiCall()
    {
        // Mục đích: 50 texts < MaxBatchSize (100) → 1 API call.
        var json = """
            {
                "embeddings": [
                    {"values":[0.1,0.2]},
                    {"values":[0.3,0.4]}
                ]
            }
            """;
        var handler = FakeHttpMessageHandler.Ok(json);
        var service = BuildService(handler, new EmbeddingOptions
        {
            ApiKey = "test-key",
            Dimensions = 2,
            MaxBatchSize = 100
        });

        var texts = Enumerable.Range(1, 2).Select(i => $"text{i}").ToList();
        var results = await service.EmbedBatchAsync(texts);

        Assert.Equal(2, results.Count);
        Assert.Equal(1, handler.CallCount);
    }

    // === #77: EmbedBatch_LargeBatch_ChunksIntoMultipleCalls ===
    [Fact]
    public async Task EmbedBatchAsync_OverBatchSize_MakesMultipleApiCalls()
    {
        // Mục đích: 250 texts, MaxBatchSize=100 → 3 API calls (100 + 100 + 50).
        var json = """{"embeddings":[{"values":[0.1]}]}""";
        var handler = FakeHttpMessageHandler.Ok(json);
        var service = BuildService(handler, new EmbeddingOptions
        {
            ApiKey = "test-key",
            Dimensions = 1,
            MaxBatchSize = 100
        });

        var texts = Enumerable.Range(1, 250).Select(i => $"text{i}").ToList();
        await service.EmbedBatchAsync(texts);

        // 250 / 100 = 2.5 → 3 chunks → 3 API calls.
        Assert.Equal(3, handler.CallCount);
    }

    // === #78: EmbedAsync_Timeout_ReturnsEmpty ===
    [Fact]
    public async Task EmbedAsync_NetworkException_ReturnsEmptyGracefully()
    {
        // Mục đích: HttpRequestException / TaskCanceledException → retry rồi trả empty.
        var handler = FakeHttpMessageHandler.Throws(new HttpRequestException("connection refused"));
        var service = BuildService(handler);

        var result = await service.EmbedAsync("test");

        Assert.Empty(result);
        // HttpRequestException trong retry list → 4 calls.
        Assert.Equal(4, handler.CallCount);
    }

    // === #79: EmbedAsync_EmptyText_HandlesGracefully ===
    [Fact]
    public async Task EmbedAsync_EmptyText_StillCallsApi()
    {
        // Mục đích: "" input không crash — service không validate text, chỉ forward.
        // Test verify rằng API được gọi (không early return) và response được parse.
        var json = """{"embedding":{"values":[]}}""";
        var handler = FakeHttpMessageHandler.Ok(json);
        var service = BuildService(handler);

        var result = await service.EmbedAsync("");

        Assert.Empty(result);
        Assert.Equal(1, handler.CallCount);
    }

    // === Bonus: EmbedAsync_MalformedJson_ReturnsEmpty ===
    [Fact]
    public async Task EmbedAsync_MalformedJsonResponse_ReturnsEmpty()
    {
        // Mục đích: Response 200 nhưng JSON không parse được → catch + empty.
        var handler = FakeHttpMessageHandler.Ok("{not json}");
        var service = BuildService(handler);

        var result = await service.EmbedAsync("test");

        Assert.Empty(result);
    }

    // === Bonus: EmbedBatch_5xxError_ReturnsEmptyPerText ===
    [Fact]
    public async Task EmbedBatchAsync_ServerError_ReturnsEmptyArrayPerText()
    {
        // Mục đích: Batch call fails → phải trả empty[] cho mỗi text (không drop items).
        var handler = FakeHttpMessageHandler.Error(HttpStatusCode.InternalServerError);
        var service = BuildService(handler, new EmbeddingOptions
        {
            ApiKey = "test-key",
            Dimensions = 2,
            MaxBatchSize = 100
        });

        var texts = new[] { "a", "b", "c" };
        var results = await service.EmbedBatchAsync(texts);

        // Phải trả đủ 3 items (mỗi item là empty array).
        Assert.Equal(3, results.Count);
        Assert.All(results, r => Assert.Empty(r));
    }
}
