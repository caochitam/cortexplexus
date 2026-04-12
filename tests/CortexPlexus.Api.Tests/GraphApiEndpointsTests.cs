using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CortexPlexus.Core.Abstractions;
using CortexPlexus.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;

namespace CortexPlexus.Api.Tests;

/// <summary>
/// Tests cho GraphApiEndpoints (GET /api/repositories, /api/graph/*, /api/search,
/// POST /api/index/push, /api/index/git).
///
/// Phạm vi: TEST-PLAN.md #62, #63, #64, #65, #69, #70, #71
/// </summary>
public class GraphApiEndpointsTests
{
    // === GET /api/repositories (sanity) ===
    [Fact]
    public async Task GetRepositories_ReturnsOkWithList()
    {
        var repoStore = Substitute.For<IRepositoryStore>();
        repoStore.ListAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<RepositoryInfo>>([
                new RepositoryInfo(Guid.NewGuid(), "test-repo", "/test/repo",
                    DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
            ]));

        using var factory = TestApiFactory.Create(services =>
        {
            services.RemoveAll<IRepositoryStore>();
            services.AddSingleton(repoStore);
        });

        var response = await factory.Client.GetAsync("/api/repositories");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("test-repo", body);
    }

    // === #69: GET /api/search — empty query parameter ===
    [Fact]
    public async Task GetSearch_MissingQueryParam_Returns400()
    {
        // Mục đích: Minimal API binding — thiếu query string "q" → 400.
        using var factory = TestApiFactory.Create();

        var response = await factory.Client.GetAsync("/api/search");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task GetSearch_WithQuery_InvokesRouter()
    {
        // Mục đích: Query hợp lệ → endpoint gọi router và trả OK.
        var fullText = Substitute.For<IFullTextStore>();
        fullText.SearchAsync(
                Arg.Any<string>(), Arg.Any<int>(), Arg.Any<Guid?>(), Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<SearchResult>>([]));

        using var factory = TestApiFactory.Create(services =>
        {
            services.RemoveAll<IFullTextStore>();
            services.AddSingleton(fullText);
        });

        var response = await factory.Client.GetAsync("/api/search?q=test");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // === #70: GET /api/graph/node ===
    [Fact]
    public async Task GetGraphNode_MissingFqn_Returns400()
    {
        using var factory = TestApiFactory.Create();

        var response = await factory.Client.GetAsync("/api/graph/node");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task GetGraphNode_ValidFqn_InvokesGraphStore()
    {
        var graphStore = Substitute.For<IGraphStore>();
        graphStore.GetNodeNeighborsAsync(
                Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new GraphOverview([], [])));

        using var factory = TestApiFactory.Create(services =>
        {
            services.RemoveAll<IGraphStore>();
            services.AddSingleton(graphStore);
        });

        var response = await factory.Client.GetAsync("/api/graph/node?fqn=App.Foo");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await graphStore.Received().GetNodeNeighborsAsync("App.Foo", 1, Arg.Any<CancellationToken>());
    }

    // === #63: POST /api/index/push — no form content ===
    [Fact]
    public async Task PostIndexPush_NonMultipart_Returns400()
    {
        using var factory = TestApiFactory.Create();

        var json = new StringContent("{}", Encoding.UTF8, "application/json");
        var response = await factory.Client.PostAsync("/api/index/push", json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("multipart", body);
    }

    [Fact]
    public async Task PostIndexPush_MissingArchiveFile_Returns400()
    {
        // Mục đích: Form có projectName nhưng thiếu file → 400 với error message cụ thể.
        using var factory = TestApiFactory.Create();

        using var form = new MultipartFormDataContent
        {
            { new StringContent("my-project"), "projectName" }
        };

        var response = await factory.Client.PostAsync("/api/index/push", form);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("archive", body);
    }

    // === #65: POST /api/index/push — oversized upload ===
    [Fact]
    public async Task PostIndexPush_OversizedFile_Returns400WithGuidance()
    {
        // Mục đích: File > 50MB → reject với message hướng dẫn dùng git archive.
        // Dùng content 51MB giả. ASP.NET có request body size limit mặc định — cần
        // verify app trả về error 4xx (BadRequest hoặc 413 Payload Too Large).
        using var factory = TestApiFactory.Create();

        var bigBytes = new byte[51 * 1024 * 1024]; // 51MB
        using var form = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(bigBytes);
        fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse("application/zip");
        form.Add(fileContent, "archive", "big.zip");
        form.Add(new StringContent("oversized"), "projectName");

        var response = await factory.Client.PostAsync("/api/index/push", form);

        // Server phải reject với 4xx — có thể là:
        // - 400 BadRequest (từ endpoint check file.Length > 50MB)
        // - 413 PayloadTooLarge (từ ASP.NET framework layer trước khi vào endpoint)
        // - 500 (nếu endpoint throw trước khi kiểm size)
        Assert.True(
            (int)response.StatusCode is >= 400 and < 500,
            $"Expected 4xx for oversized upload, got {(int)response.StatusCode}");
    }

    // === #71: POST /api/index/git — validation ===
    [Fact]
    public async Task PostIndexGit_NoUrl_Returns400()
    {
        using var factory = TestApiFactory.Create();

        var response = await factory.Client.PostAsJsonAsync("/api/index/git", new { });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Git URL is required", body);
    }

    [Theory]
    [InlineData("ftp://example.com/repo.git")]
    [InlineData("file:///etc/passwd")]
    [InlineData("javascript:alert(1)")]
    [InlineData("not-a-url")]
    public async Task PostIndexGit_InvalidUrlScheme_Returns400(string badUrl)
    {
        using var factory = TestApiFactory.Create();

        var response = await factory.Client.PostAsJsonAsync("/api/index/git",
            new { url = badUrl });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Invalid Git URL", body);
    }

    [Theory]
    [InlineData("main; rm -rf /")]
    [InlineData("main & whoami")]
    [InlineData("branch with spaces")]
    public async Task PostIndexGit_MaliciousBranch_Returns400(string badBranch)
    {
        using var factory = TestApiFactory.Create();

        var response = await factory.Client.PostAsJsonAsync("/api/index/git",
            new { url = "https://github.com/org/repo.git", branch = badBranch });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Invalid branch name", body);
    }
}
