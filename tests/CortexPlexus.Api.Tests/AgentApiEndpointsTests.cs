using System.Net;
using System.Text.Json;

namespace CortexPlexus.Api.Tests;

/// <summary>
/// Tests cho AgentApiEndpoints (GET /api/agent/*).
///
/// Phạm vi: TEST-PLAN.md #66, #67, #68
///
/// Tập trung vào security (platform allowlist, path traversal).
/// </summary>
public class AgentApiEndpointsTests
{
    // === #66: GET_AgentDownload_ValidPlatform_ReturnsFile ===
    [Fact]
    public async Task GetAgentDownload_ValidPlatform_NoFileExists_Returns404()
    {
        // Mục đích: Platform hợp lệ nhưng file binary không tồn tại → 404 chứ không 500.
        // Workspace path default là "/workspace" — không có trong test env → NotFound.
        using var factory = TestApiFactory.Create();

        var response = await factory.Client.GetAsync("/api/agent/download?platform=linux-x64");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("linux-x64", body); // error message nói rõ platform không có file
    }

    // === #67: GET_AgentDownload_InvalidPlatform_Returns400 ===
    [Theory]
    [InlineData("hacked")]
    [InlineData("arm64")] // không trong allowlist
    [InlineData("win-x86")] // không trong allowlist
    [InlineData("")]
    public async Task GetAgentDownload_InvalidPlatform_Returns400(string badPlatform)
    {
        using var factory = TestApiFactory.Create();

        var response = await factory.Client.GetAsync($"/api/agent/download?platform={badPlatform}");

        // Empty platform → auto-detect logic trong endpoint, có thể trả khác. Skip empty.
        if (string.IsNullOrEmpty(badPlatform))
        {
            // DetectPlatform() trả linux-x64 / win-x64 / osx-x64 tuỳ host
            // → 404 (không tìm thấy file) hoặc 400 nếu runtime lạ — cả 2 đều không phải 500.
            Assert.True(
                response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest,
                $"Empty platform should map to safe status (400/404), got {response.StatusCode}");
            return;
        }

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Invalid platform", body);
        Assert.Contains("Allowed", body); // error message liệt kê allowed values
    }

    // === #68: GET_AgentDownload_PathTraversal_Rejected ===
    [Theory]
    [InlineData("../../../etc/passwd")]
    [InlineData("..\\..\\windows\\system32")]
    [InlineData("/absolute/path")]
    [InlineData("linux-x64/../../etc")]
    public async Task GetAgentDownload_PathTraversalAttempt_Rejected(string maliciousPlatform)
    {
        // Mục đích: Path traversal attempts phải bị reject bởi allowlist check.
        using var factory = TestApiFactory.Create();

        var response = await factory.Client.GetAsync(
            $"/api/agent/download?platform={Uri.EscapeDataString(maliciousPlatform)}");

        // Allowlist chặn → 400 BadRequest.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Invalid platform", body);
    }

    [Fact]
    public async Task GetAgentVersion_ReturnsVersionInfo()
    {
        // Sanity test: /api/agent/version luôn trả JSON với version field.
        using var factory = TestApiFactory.Create();

        var response = await factory.Client.GetAsync("/api/agent/version");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("version", out _));
        Assert.True(doc.RootElement.TryGetProperty("platforms", out var platforms));

        // Allowed platforms list
        var platformList = platforms.EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains("win-x64", platformList);
        Assert.Contains("linux-x64", platformList);
        Assert.Contains("osx-x64", platformList);
    }

    [Fact]
    public async Task GetAgentInstallScriptBash_ReturnsShellScript()
    {
        // /api/agent/install.sh phải trả text/plain với shebang.
        using var factory = TestApiFactory.Create();

        var response = await factory.Client.GetAsync("/api/agent/install.sh");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.StartsWith("#!/bin/bash", body);
        Assert.Contains("cortexplexus-agent", body);
    }
}
