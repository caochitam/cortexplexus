using System.Net;
using System.Text;

namespace CortexPlexus.Embedding.Tests;

/// <summary>
/// HttpMessageHandler mock cho phép test mà không chạy HTTP thật.
/// Hỗ trợ:
/// - Trả response cố định cho tất cả request
/// - Trả sequence of responses (cho retry tests)
/// - Count số lần được gọi
/// - Verify request payload
///
/// Cách dùng:
/// <code>
/// var handler = FakeHttpMessageHandler.Ok(new { embeddings = new[] { new[] { 0.1f } } });
/// var client = new HttpClient(handler);
/// // ...
/// Assert.Equal(1, handler.CallCount);
/// </code>
/// </summary>
public sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responses;

    public int CallCount { get; private set; }
    public List<HttpRequestMessage> RequestsReceived { get; } = new();

    public FakeHttpMessageHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] responses)
    {
        _responses = new Queue<Func<HttpRequestMessage, HttpResponseMessage>>(responses);
    }

    /// <summary>Factory: trả status 200 với JSON body cố định cho mọi request.</summary>
    public static FakeHttpMessageHandler Ok(string jsonBody)
        => new(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(jsonBody, Encoding.UTF8, "application/json")
        });

    /// <summary>Factory: trả status error cố định.</summary>
    public static FakeHttpMessageHandler Error(HttpStatusCode statusCode, string body = "")
        => new(_ => new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        });

    /// <summary>Factory: sequence — call 1 trả response[0], call 2 trả response[1], ...</summary>
    public static FakeHttpMessageHandler Sequence(params HttpResponseMessage[] responses)
    {
        var queue = new Queue<HttpResponseMessage>(responses);
        return new FakeHttpMessageHandler(_ =>
        {
            if (queue.Count == 0)
                throw new InvalidOperationException("FakeHttpMessageHandler: exhausted response queue");
            return queue.Dequeue();
        });
    }

    /// <summary>Factory: throws exception cho mỗi request (simulate network failure).</summary>
    public static FakeHttpMessageHandler Throws(Exception ex)
        => new(_ => throw ex);

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        CallCount++;
        RequestsReceived.Add(request);

        if (_responses.Count == 0)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("FakeHttpMessageHandler: no more responses configured")
            });
        }

        // Peek the first function — single-function handlers reuse it for all calls,
        // multi-function handlers pop each one.
        var responseFunc = _responses.Count == 1 ? _responses.Peek() : _responses.Dequeue();

        try
        {
            var response = responseFunc(request);
            return Task.FromResult(response);
        }
        catch (Exception ex)
        {
            return Task.FromException<HttpResponseMessage>(ex);
        }
    }

    /// <summary>Build HttpClient dùng handler này — không cần IHttpClientFactory thật.</summary>
    public HttpClient BuildHttpClient() => new(this, disposeHandler: false);
}

/// <summary>
/// Minimal IHttpClientFactory cho tests — trả HttpClient dùng FakeHttpMessageHandler.
/// Đơn giản hơn Substitute.For&lt;IHttpClientFactory&gt; vì không cần setup callbacks.
/// </summary>
public sealed class FakeHttpClientFactory : IHttpClientFactory
{
    private readonly FakeHttpMessageHandler _handler;

    public FakeHttpClientFactory(FakeHttpMessageHandler handler)
    {
        _handler = handler;
    }

    public HttpClient CreateClient(string name) => _handler.BuildHttpClient();
}
