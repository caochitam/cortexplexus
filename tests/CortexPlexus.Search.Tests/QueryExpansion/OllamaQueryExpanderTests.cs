using System.Net;
using System.Text.Json;
using CortexPlexus.Search.QueryExpansion;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CortexPlexus.Search.Tests.QueryExpansion;

public sealed class OllamaQueryExpanderTests
{
    private static OllamaQueryExpander CreateExpander(
        HttpResponseMessage response,
        bool enabled = true,
        int timeoutSeconds = 10)
    {
        var handler = new FakeHttpHandler(response);
        var factory = new FakeHttpClientFactory(handler);
        var options = Options.Create(new QueryExpansionOptions
        {
            Enabled = enabled,
            Provider = "ollama",
            OllamaBaseUrl = "http://localhost:11434",
            OllamaModel = "phi3:mini",
            TimeoutSeconds = timeoutSeconds
        });
        return new OllamaQueryExpander(factory, options, NullLogger<OllamaQueryExpander>.Instance);
    }

    private static HttpResponseMessage OkResponse(string responseText) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { response = responseText }),
                System.Text.Encoding.UTF8,
                "application/json")
        };

    [Fact]
    public void IsEnabled_WhenConfiguredEnabled_ReturnsTrue()
    {
        var expander = CreateExpander(OkResponse("test"), enabled: true);
        Assert.True(expander.IsEnabled);
    }

    [Fact]
    public void IsEnabled_WhenConfiguredDisabled_ReturnsFalse()
    {
        var expander = CreateExpander(OkResponse("test"), enabled: false);
        Assert.False(expander.IsEnabled);
    }

    [Fact]
    public async Task ExpandHydeAsync_ReturnsHypotheticalDocument()
    {
        var hypothetical = "PaymentService processes credit card transactions using Stripe API with retry logic.";
        var expander = CreateExpander(OkResponse(hypothetical));

        var result = await expander.ExpandHydeAsync("payment processing");

        Assert.NotNull(result);
        Assert.Contains("PaymentService", result);
    }

    [Fact]
    public async Task ExpandHydeAsync_EmptyResponse_ReturnsNull()
    {
        var expander = CreateExpander(OkResponse("   "));

        var result = await expander.ExpandHydeAsync("test query");

        Assert.Null(result);
    }

    [Fact]
    public async Task ExpandHydeAsync_ApiError_ReturnsNull()
    {
        var response = new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("error")
        };
        var expander = CreateExpander(response);

        var result = await expander.ExpandHydeAsync("test query");

        Assert.Null(result);
    }

    [Fact]
    public async Task ExpandMultiQueryAsync_ParsesMultipleVariants()
    {
        var responseText = """
            payment error handling strategies
            how PaymentService handles failed transactions
            retry logic for payment processing errors
            """;
        var expander = CreateExpander(OkResponse(responseText));

        var results = await expander.ExpandMultiQueryAsync("payment errors", variants: 3);

        // Should include original query + parsed variants
        Assert.True(results.Count >= 2);
        Assert.Contains("payment errors", results);
    }

    [Fact]
    public async Task ExpandMultiQueryAsync_EmptyResponse_ReturnsOriginalQuery()
    {
        var expander = CreateExpander(OkResponse(""));

        var results = await expander.ExpandMultiQueryAsync("test query");

        Assert.Single(results);
        Assert.Equal("test query", results[0]);
    }

    [Fact]
    public async Task ExpandMultiQueryAsync_ApiError_ReturnsOriginalQuery()
    {
        var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Content = new StringContent("unavailable")
        };
        var expander = CreateExpander(response);

        var results = await expander.ExpandMultiQueryAsync("test query");

        Assert.Single(results);
        Assert.Equal("test query", results[0]);
    }

    [Fact]
    public async Task ExpandMultiQueryAsync_StripsNumberingAndBullets()
    {
        var responseText = """
            - first variant query
            * second variant query
            third variant query
            """;
        var expander = CreateExpander(OkResponse(responseText));

        var results = await expander.ExpandMultiQueryAsync("original", variants: 3);

        // None of the results should start with bullets
        foreach (var result in results)
        {
            Assert.False(result.StartsWith('-'));
            Assert.False(result.StartsWith('*'));
        }
    }

    [Fact]
    public async Task ExpandHydeAsync_NetworkError_ReturnsNull()
    {
        var handler = new ThrowingHttpHandler(new HttpRequestException("Connection refused"));
        var factory = new FakeHttpClientFactory(handler);
        var options = Options.Create(new QueryExpansionOptions
        {
            Enabled = true,
            OllamaBaseUrl = "http://localhost:11434",
            OllamaModel = "phi3:mini"
        });
        var expander = new OllamaQueryExpander(factory, options, NullLogger<OllamaQueryExpander>.Instance);

        var result = await expander.ExpandHydeAsync("test");

        Assert.Null(result);
    }

    // --- Test helpers ---

    private sealed class FakeHttpHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(response);
    }

    private sealed class ThrowingHttpHandler(Exception exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromException<HttpResponseMessage>(exception);
    }

    private sealed class FakeHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler);
    }
}
