using System.Net;
using CortexPlexus.Embedding;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CortexPlexus.Embedding.Tests;

/// <summary>
/// Tests cho OllamaEmbeddingService với HTTP mock.
///
/// Phạm vi: TEST-PLAN.md #80, #81, #82, #83
///
/// Ollama không có retry policy (khác Gemini) — 1 call/request, graceful empty
/// khi service down.
/// </summary>
public class OllamaEmbeddingServiceTests
{
    private static OllamaEmbeddingService BuildService(
        FakeHttpMessageHandler handler,
        EmbeddingOptions? options = null)
    {
        var factory = new FakeHttpClientFactory(handler);
        var opts = Options.Create(options ?? new EmbeddingOptions
        {
            Provider = "ollama",
            OllamaBaseUrl = "http://localhost:11434",
            OllamaModel = "nomic-embed-text",
            Dimensions = 768
        });
        return new OllamaEmbeddingService(factory, opts, NullLogger<OllamaEmbeddingService>.Instance);
    }

    // === #80: EmbedAsync_Success_ReturnsFirstEmbedding ===
    [Fact]
    public async Task EmbedAsync_Success_ReturnsFirstEmbedding()
    {
        // Mục đích: Ollama trả { embeddings: [[...]] } — service lấy element đầu.
        var json = """{"embeddings":[[0.5,0.6,0.7]]}""";
        var handler = FakeHttpMessageHandler.Ok(json);
        var service = BuildService(handler);

        var result = await service.EmbedAsync("test");

        Assert.Equal(3, result.Length);
        Assert.Equal(0.5f, result[0]);
        Assert.Equal(0.6f, result[1]);
        Assert.Equal(0.7f, result[2]);
    }

    [Fact]
    public async Task EmbedAsync_EmptyEmbeddingsArray_ReturnsEmpty()
    {
        // Mục đích: { embeddings: [] } → trả empty (pattern match fails).
        var json = """{"embeddings":[]}""";
        var handler = FakeHttpMessageHandler.Ok(json);
        var service = BuildService(handler);

        var result = await service.EmbedAsync("test");

        Assert.Empty(result);
    }

    // === #81: EmbedAsync_OllamaDown_ReturnsEmpty ===
    [Fact]
    public async Task EmbedAsync_ConnectionRefused_ReturnsEmpty()
    {
        // Mục đích: Ollama service down → HttpRequestException → catch → empty.
        // Khác Gemini: Ollama không có retry, chỉ 1 call.
        var handler = FakeHttpMessageHandler.Throws(new HttpRequestException("connection refused"));
        var service = BuildService(handler);

        var result = await service.EmbedAsync("test");

        Assert.Empty(result);
        Assert.Equal(1, handler.CallCount); // không retry
    }

    // === #82: EmbedAsync_InvalidModel_ReturnsEmpty ===
    [Fact]
    public async Task EmbedAsync_404ModelNotFound_ReturnsEmpty()
    {
        // Mục đích: Ollama 404 khi model không tồn tại → graceful empty.
        var handler = FakeHttpMessageHandler.Error(HttpStatusCode.NotFound, """{"error":"model not found"}""");
        var service = BuildService(handler);

        var result = await service.EmbedAsync("test");

        Assert.Empty(result);
    }

    [Fact]
    public async Task EmbedAsync_500ServerError_ReturnsEmpty()
    {
        var handler = FakeHttpMessageHandler.Error(HttpStatusCode.InternalServerError);
        var service = BuildService(handler);

        var result = await service.EmbedAsync("test");

        Assert.Empty(result);
        // Ollama không retry → 1 call.
        Assert.Equal(1, handler.CallCount);
    }

    // === #83: EmbedBatch_ReturnsMultipleEmbeddings ===
    [Fact]
    public async Task EmbedBatchAsync_Success_ReturnsAllEmbeddings()
    {
        // Mục đích: Batch trả về đủ số lượng embeddings tương ứng với inputs.
        var json = """
            {
                "embeddings": [
                    [0.1, 0.2],
                    [0.3, 0.4],
                    [0.5, 0.6]
                ]
            }
            """;
        var handler = FakeHttpMessageHandler.Ok(json);
        var service = BuildService(handler);

        var texts = new[] { "a", "b", "c" };
        var results = await service.EmbedBatchAsync(texts);

        Assert.Equal(3, results.Count);
        Assert.Equal(0.1f, results[0][0]);
        Assert.Equal(0.3f, results[1][0]);
        Assert.Equal(0.5f, results[2][0]);
    }

    [Fact]
    public async Task EmbedBatchAsync_ServerDown_ReturnsEmptyArrayPerText()
    {
        // Mục đích: Batch fails → mỗi item trả empty array (không drop).
        var handler = FakeHttpMessageHandler.Throws(new HttpRequestException("down"));
        var service = BuildService(handler);

        var texts = new[] { "a", "b", "c" };
        var results = await service.EmbedBatchAsync(texts);

        Assert.Equal(3, results.Count);
        Assert.All(results, r => Assert.Empty(r));
    }

    // === Bonus: BaseUrl trailing slash normalization ===
    [Fact]
    public async Task EmbedAsync_BaseUrlWithTrailingSlash_NormalizesCorrectly()
    {
        // Mục đích: OllamaBaseUrl với trailing "/" không tạo "//api/embed".
        var handler = FakeHttpMessageHandler.Ok("""{"embeddings":[[1.0]]}""");
        var service = BuildService(handler, new EmbeddingOptions
        {
            OllamaBaseUrl = "http://localhost:11434/", // trailing slash
            OllamaModel = "test-model",
            Dimensions = 1
        });

        var result = await service.EmbedAsync("test");

        Assert.Single(result);
        // Verify request URL không có "//"
        var request = handler.RequestsReceived[0];
        Assert.NotNull(request.RequestUri);
        Assert.Equal("http://localhost:11434/api/embed", request.RequestUri.ToString());
    }
}
