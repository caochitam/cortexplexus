using CortexPlexus.Agent;

namespace CortexPlexus.Agent.Tests;

/// <summary>
/// Tests cho ProjectFileWatcher.IsWatchedFile — pure filter logic.
///
/// Phạm vi: TEST-PLAN.md #94, #95, #96
///
/// Lưu ý: debounce behavior (#92, #93) cần real FileSystemWatcher → flaky test.
/// Tôi thay thế bằng path filter tests (deterministic, sub-ms).
/// </summary>
public class ProjectFileWatcherTests
{
    // === #95: Filter_WatchedExtensions_Only ===
    [Theory]
    [InlineData("file.cs", true)]
    [InlineData("file.ts", true)]
    [InlineData("file.tsx", true)]
    [InlineData("file.js", true)]
    [InlineData("file.jsx", true)]
    [InlineData("file.py", true)]
    [InlineData("file.md", true)]
    public void IsWatchedFile_WatchedExtensions_ReturnsTrue(string filename, bool expected)
    {
        // Mục đích: Tất cả extension trong WatchedExtensions được chấp nhận.
        var result = ProjectFileWatcher.IsWatchedFile(Path.Combine("/repo", filename));
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("file.jpg")]
    [InlineData("file.png")]
    [InlineData("file.dll")]
    [InlineData("file.exe")]
    [InlineData("file.pdb")]
    [InlineData("file.json")]
    [InlineData("file.xml")]
    [InlineData("file")] // no extension
    public void IsWatchedFile_NonWatchedExtensions_ReturnsFalse(string filename)
    {
        // Mục đích: Extension không trong watchlist → bị filter ra.
        var result = ProjectFileWatcher.IsWatchedFile(Path.Combine("/repo", filename));
        Assert.False(result);
    }

    // === #96: Filter_CaseInsensitive ===
    [Theory]
    [InlineData("file.CS")]
    [InlineData("file.TS")]
    [InlineData("file.Py")]
    [InlineData("FILE.CS")]
    public void IsWatchedFile_CaseInsensitive_Accepted(string filename)
    {
        // Mục đích: Extension matching là case-insensitive.
        var result = ProjectFileWatcher.IsWatchedFile(Path.Combine("/repo", filename));
        Assert.True(result);
    }

    // === #94: Filter_ExcludedDirs_Ignored ===
    [Theory]
    [InlineData("/repo/bin/Debug/App.cs")]
    [InlineData("/repo/obj/project.cs")]
    [InlineData("/repo/node_modules/lib/index.ts")]
    [InlineData("/repo/.git/config.cs")]
    [InlineData("/repo/.venv/site-packages/mod.py")]
    [InlineData("/repo/__pycache__/mod.py")]
    [InlineData("/repo/dist/bundle.js")]
    [InlineData("/repo/.vs/settings.cs")]
    [InlineData("/repo/.idea/config.cs")]
    [InlineData("/repo/out/build.cs")]
    [InlineData("/repo/build/output.cs")]
    public void IsWatchedFile_InExcludedDirectory_ReturnsFalse(string fullPath)
    {
        // Mục đích: Files trong build/dependency dirs bị filter ra dù extension hợp lệ.
        var result = ProjectFileWatcher.IsWatchedFile(fullPath);
        Assert.False(result);
    }

    [Theory]
    [InlineData("/repo/BIN/App.cs")]
    [InlineData("/repo/NODE_MODULES/lib.ts")]
    [InlineData("/repo/Node_Modules/lib.ts")]
    public void IsWatchedFile_ExcludedDirs_CaseInsensitive(string fullPath)
    {
        // Mục đích: Excluded dir matching cũng là case-insensitive.
        var result = ProjectFileWatcher.IsWatchedFile(fullPath);
        Assert.False(result);
    }

    [Fact]
    public void IsWatchedFile_NestedDeep_StillRespectsExclusion()
    {
        // Mục đích: Exclusion dir ở giữa path vẫn filter (không chỉ top-level).
        var path = Path.Combine("/repo", "src", "SubProject", "bin", "Debug", "Service.cs");
        Assert.False(ProjectFileWatcher.IsWatchedFile(path));
    }

    [Fact]
    public void IsWatchedFile_NormalSourceFile_Accepted()
    {
        // Mục đích: Sanity — happy path file thực sự được accept.
        var path = Path.Combine("/repo", "src", "Services", "PaymentService.cs");
        Assert.True(ProjectFileWatcher.IsWatchedFile(path));
    }
}
