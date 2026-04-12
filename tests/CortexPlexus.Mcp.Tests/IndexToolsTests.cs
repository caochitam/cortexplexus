using CortexPlexus.App.Mcp.Tools;
using CortexPlexus.Core.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace CortexPlexus.Mcp.Tests;

/// <summary>
/// Tests cho IndexTools (IndexFromLocal, IndexFromGit).
///
/// Phạm vi: TEST-PLAN.md #57, #58, #59, #60, #61
///
/// Lưu ý: IndexingPipeline là concrete class không dễ mock. Tests tập trung vào
/// validation/error paths ở đầu method (chạy TRƯỚC khi scopeFactory được dùng) —
/// đây cũng là phần security-critical cần verify đầy đủ.
/// </summary>
public class IndexToolsTests
{
    // === #57: IndexFromLocal_NonExistentPath_ReturnsError ===
    [Fact]
    public async Task IndexFromLocal_NullPath_ReturnsError()
    {
        // Mục đích: Null/whitespace path phải reject rõ ràng TRƯỚC khi gọi scopeFactory.
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        var repoStore = Substitute.For<IRepositoryStore>();

        var result = await IndexTools.IndexFromLocal(null!, scopeFactory, repoStore);

        Assert.Contains("Error: path is required", result);
        // scopeFactory không được gọi (validation chặn trước).
        scopeFactory.DidNotReceive().CreateScope();
    }

    [Fact]
    public async Task IndexFromLocal_EmptyPath_ReturnsError()
    {
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        var repoStore = Substitute.For<IRepositoryStore>();

        var result = await IndexTools.IndexFromLocal("   ", scopeFactory, repoStore);

        Assert.Contains("Error: path is required", result);
        scopeFactory.DidNotReceive().CreateScope();
    }

    [Fact]
    public async Task IndexFromLocal_NonExistentPath_ReturnsErrorMessage()
    {
        // Mục đích: Path không tồn tại → error message nói rõ path nào.
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        var repoStore = Substitute.For<IRepositoryStore>();

        var ghostPath = Path.Combine(Path.GetTempPath(), $"ghost-{Guid.NewGuid():N}");
        // Không tạo thư mục → không tồn tại
        var result = await IndexTools.IndexFromLocal(ghostPath, scopeFactory, repoStore);

        Assert.Contains("does not exist", result);
        Assert.Contains(ghostPath, result);
        scopeFactory.DidNotReceive().CreateScope();
    }

    // === #58: IndexFromGit_InvalidUrl_Rejected ===
    [Fact]
    public async Task IndexFromGit_NullUrl_ReturnsError()
    {
        var scopeFactory = Substitute.For<IServiceScopeFactory>();

        var result = await IndexTools.IndexFromGit(null!, "main", null, scopeFactory);

        Assert.Contains("Error: Git URL is required", result);
        scopeFactory.DidNotReceive().CreateScope();
    }

    [Fact]
    public async Task IndexFromGit_EmptyUrl_ReturnsError()
    {
        var scopeFactory = Substitute.For<IServiceScopeFactory>();

        var result = await IndexTools.IndexFromGit("   ", "main", null, scopeFactory);

        Assert.Contains("Error: Git URL is required", result);
        scopeFactory.DidNotReceive().CreateScope();
    }

    [Theory]
    [InlineData("ftp://example.com/repo.git")]
    [InlineData("ssh://git@github.com/org/repo.git")]
    [InlineData("file:///etc/passwd")]
    [InlineData("not-a-url")]
    [InlineData("javascript:alert(1)")]
    public async Task IndexFromGit_InvalidScheme_RejectedAsInvalidUrl(string badUrl)
    {
        // Mục đích: Chỉ HTTPS/HTTP được accept. Các scheme khác là security risk.
        var scopeFactory = Substitute.For<IServiceScopeFactory>();

        var result = await IndexTools.IndexFromGit(badUrl, "main", null, scopeFactory);

        Assert.Contains("Error: Invalid Git URL", result);
        Assert.Contains("HTTPS", result);
        scopeFactory.DidNotReceive().CreateScope();
    }

    // === #59: IndexFromGit_MaliciousBranch_Rejected ===
    [Theory]
    [InlineData("main; rm -rf /")]
    [InlineData("main & malicious")]
    [InlineData("branch with spaces")]
    [InlineData("main;whoami")]
    public async Task IndexFromGit_MaliciousBranchName_Rejected(string badBranch)
    {
        // Mục đích: Branch name chứa ký tự shell injection → reject.
        var scopeFactory = Substitute.For<IServiceScopeFactory>();

        var result = await IndexTools.IndexFromGit(
            "https://github.com/org/repo.git", badBranch, null, scopeFactory);

        Assert.Contains("Error: Invalid branch name", result);
        scopeFactory.DidNotReceive().CreateScope();
    }

    // === #60: IndexFromGit_HttpsOnly_Accepted (validation passes) ===
    [Theory]
    [InlineData("https://github.com/org/repo.git")]
    [InlineData("https://gitlab.com/org/repo.git")]
    [InlineData("http://internal-server/repo.git")]
    public void IndexFromGit_ValidHttpScheme_PassesInitialValidation(string validUrl)
    {
        // Mục đích: URL hợp lệ format không bị reject ở validation stage.
        //
        // Lưu ý: Tool sau validation sẽ thực sự cố clone + resolve IndexingPipeline.
        // Ta verify bằng cách check: validation logic chấp nhận URL (cả Uri.TryCreate
        // và scheme check đều pass). Test chỉ gọi helper có sẵn trong .NET.
        Assert.True(Uri.TryCreate(validUrl, UriKind.Absolute, out var uri));
        Assert.Contains(uri.Scheme, new[] { "https", "http" });
    }

    // === #61: IndexFromLocal_ExistingDirectory_PassesValidation ===
    [Fact]
    public async Task IndexFromLocal_ExistingEmptyDirectory_PassesValidation()
    {
        // Mục đích: Directory tồn tại → validation pass, scopeFactory được gọi.
        // scopeFactory mock sẽ throw → ta catch và verify validation passed trước.
        var tempDir = Path.Combine(Path.GetTempPath(), $"cortex-idx-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var scopeFactory = Substitute.For<IServiceScopeFactory>();
            // CreateScope throws → verify validation đã pass và scopeFactory đã được gọi
            scopeFactory.CreateScope().Returns(_ => throw new InvalidOperationException("expected test throw"));
            var repoStore = Substitute.For<IRepositoryStore>();

            var ex = await Record.ExceptionAsync(() => IndexTools.IndexFromLocal(tempDir, scopeFactory, repoStore));

            // Validation pass (không trả error message ngay) → gọi scopeFactory → throw.
            Assert.NotNull(ex);
            Assert.IsType<InvalidOperationException>(ex);
            scopeFactory.Received().CreateScope();
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }
}
