using CortexPlexus.Agent;
using CortexPlexus.Core;

namespace CortexPlexus.Agent.Tests;

/// <summary>
/// Tests cho AgentUpdater pure logic methods.
///
/// Phạm vi: TEST-PLAN.md #97, #98, #99, #100
///
/// Chiến lược: test các pure functions (ParseVersionResponse, ShouldUpdate,
/// VerifySha256, DetectPlatform) mà không chạy real HTTP / filesystem.
/// </summary>
public class AgentUpdaterTests
{
    // === #97: CheckUpdate_NewVersion_Downloads ===
    // Core logic: ShouldUpdate(serverVersion, currentVersion)
    [Fact]
    public void ShouldUpdate_NewVersion_ReturnsTrue()
    {
        // Mục đích: Server trả version mới hơn → cần update.
        Assert.True(AgentUpdater.ShouldUpdate("1.0.1", "1.0.0"));
        Assert.True(AgentUpdater.ShouldUpdate("2.0.0", "1.0.0"));
    }

    // === #98: CheckUpdate_SameVersion_Skips ===
    [Fact]
    public void ShouldUpdate_SameVersion_ReturnsFalse()
    {
        // Mục đích: Cùng version → không update.
        Assert.False(AgentUpdater.ShouldUpdate("1.0.0", "1.0.0"));
    }

    [Fact]
    public void ShouldUpdate_NullOrEmptyServerVersion_ReturnsFalse()
    {
        // Mục đích: Server không trả version → không update (safe default).
        Assert.False(AgentUpdater.ShouldUpdate(null, "1.0.0"));
        Assert.False(AgentUpdater.ShouldUpdate("", "1.0.0"));
        Assert.False(AgentUpdater.ShouldUpdate("   ", "1.0.0"));
    }

    // === #99: CheckUpdate_HashMismatch_Aborts ===
    [Fact]
    public void VerifySha256_CorrectHash_ReturnsTrue()
    {
        // Mục đích: Hash khớp → verify pass.
        var bytes = "test binary content"u8.ToArray();
        // SHA256 của chuỗi này computed thủ công:
        var expectedHash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(bytes));

        Assert.True(AgentUpdater.VerifySha256(bytes, expectedHash));
    }

    [Fact]
    public void VerifySha256_IncorrectHash_ReturnsFalse()
    {
        // Mục đích: Hash không khớp → verify fail → update phải bị abort.
        var bytes = "test binary content"u8.ToArray();
        var wrongHash = new string('A', 64);

        Assert.False(AgentUpdater.VerifySha256(bytes, wrongHash));
    }

    [Fact]
    public void VerifySha256_CaseInsensitive()
    {
        // Mục đích: Hash comparison là case-insensitive (server có thể trả uppercase/lowercase).
        var bytes = "data"u8.ToArray();
        var hashUpper = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(bytes));
        var hashLower = hashUpper.ToLowerInvariant();

        Assert.True(AgentUpdater.VerifySha256(bytes, hashUpper));
        Assert.True(AgentUpdater.VerifySha256(bytes, hashLower));
    }

    // === ParseVersionResponse tests — happy path + edge cases ===
    [Fact]
    public void ParseVersionResponse_CompleteResponse_ExtractsVersionAndHash()
    {
        var json = """
            {
                "version": "1.2.3",
                "platforms": ["win-x64", "linux-x64", "osx-x64"],
                "sha256": {
                    "win-x64": "ABC123",
                    "linux-x64": "DEF456",
                    "osx-x64": "GHI789"
                }
            }
            """;

        var (version, hash) = AgentUpdater.ParseVersionResponse(json, "linux-x64");

        Assert.Equal("1.2.3", version);
        Assert.Equal("DEF456", hash);
    }

    [Fact]
    public void ParseVersionResponse_MissingSha256_ReturnsVersionWithNullHash()
    {
        // Mục đích: Response chỉ có version, không có sha256 → hash=null
        // (update flow sẽ skip verification).
        var json = """{"version":"1.0.0","platforms":["linux-x64"]}""";

        var (version, hash) = AgentUpdater.ParseVersionResponse(json, "linux-x64");

        Assert.Equal("1.0.0", version);
        Assert.Null(hash);
    }

    [Fact]
    public void ParseVersionResponse_UnknownPlatform_ReturnsNullHash()
    {
        // Mục đích: Platform request không có trong sha256 dict → hash=null.
        var json = """
            {
                "version": "1.0.0",
                "sha256": {
                    "win-x64": "ABC"
                }
            }
            """;

        var (version, hash) = AgentUpdater.ParseVersionResponse(json, "osx-x64");

        Assert.Equal("1.0.0", version);
        Assert.Null(hash);
    }

    // === #100: CheckUpdate_ServerDown_ContinuesRunning ===
    // CheckAndUpdateAsync có try/catch bao ngoài — ServerDown (network fail) → return false, không throw.
    // Test behavior qua HttpClient mock.
    [Fact]
    public async Task CheckAndUpdateAsync_ServerDown_ReturnsFalseGracefully()
    {
        // Mục đích: HttpClient throws network error → CheckAndUpdateAsync catch và return false.
        // (Agent tiếp tục chạy bình thường, chỉ là không update).

        // Tạo HttpClient with fake handler throws exception.
        var handler = new ThrowingHttpHandler(new HttpRequestException("connection refused"));
        var httpClient = new HttpClient(handler);

        // Use internal test constructor.
        var updater = new AgentUpdater(
            "http://localhost:1234",
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance,
            httpClient);

        var result = await updater.CheckAndUpdateAsync();

        Assert.False(result); // không throw, trả false
    }

    [Fact]
    public async Task CheckAndUpdateAsync_Http500_ReturnsFalseGracefully()
    {
        // Mục đích: Server 500 → CheckAndUpdateAsync return false (không throw).
        var handler = new FixedStatusHttpHandler(System.Net.HttpStatusCode.InternalServerError);
        var httpClient = new HttpClient(handler);

        var updater = new AgentUpdater(
            "http://localhost:1234",
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance,
            httpClient);

        var result = await updater.CheckAndUpdateAsync();

        Assert.False(result);
    }

    [Fact]
    public async Task CheckAndUpdateAsync_SameVersion_ReturnsFalseNoDownload()
    {
        // Mục đích: Server trả version == current (AgentInfo.Version="1.0.0")
        // → skip download, return false.
        var versionJson = $"{{\"version\":\"{AgentInfo.Version}\",\"sha256\":{{}}}}";
        var handler = new JsonResponseHttpHandler(versionJson);
        var httpClient = new HttpClient(handler);

        var updater = new AgentUpdater(
            "http://localhost:1234",
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance,
            httpClient);

        var result = await updater.CheckAndUpdateAsync();

        Assert.False(result);
        Assert.Equal(1, handler.CallCount); // chỉ 1 call (version check), không download
    }

    // === DetectPlatform ===
    [Fact]
    public void DetectPlatform_ReturnsValidRid()
    {
        // Mục đích: DetectPlatform luôn trả 1 trong 3 RIDs được support.
        var platform = AgentUpdater.DetectPlatform();
        Assert.Contains(platform, new[] { "win-x64", "linux-x64", "osx-x64" });
    }

    // --- Test helper handlers ---

    private sealed class ThrowingHttpHandler(Exception ex) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromException<HttpResponseMessage>(ex);
    }

    private sealed class FixedStatusHttpHandler(System.Net.HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(status));
    }

    private sealed class JsonResponseHttpHandler(string json) : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            });
        }
    }
}
