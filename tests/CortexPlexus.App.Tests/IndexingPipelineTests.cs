using CortexPlexus.App.Indexing;

namespace CortexPlexus.App.Tests;

/// <summary>
/// Tests cho IndexingPipeline static helpers — DetectProjectName, FindAllSolutionsAndProjects,
/// IsExcludedPath.
///
/// Phạm vi: TEST-PLAN.md #84, #85, #86, #87, #88, #89, #90, #91
///
/// Lưu ý: IndexAsync end-to-end cần full DI stack (graph store, parser, embedding).
/// Tests tập trung vào pure helpers có thể verify deterministic mà không cần DB/parser.
/// Incremental indexing behavior (#84, #85) được test indirectly qua RepositoryStore tests trong Sprint 2.
/// </summary>
public class IndexingPipelineTests
{
    // Helper: tạo temp dir với files, return (dir, cleanup).
    private static (string dir, Action cleanup) CreateTempDir(params (string path, string content)[] files)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"cortex-pipe-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);

        foreach (var (rel, content) in files)
        {
            var full = Path.Combine(dir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }

        return (dir, () => Directory.Delete(dir, recursive: true));
    }

    // === #86 partial: DetectProjectName — .sln priority (C# project) ===
    [Fact]
    public void DetectProjectName_SolutionFile_ReturnsSlnName()
    {
        var (dir, cleanup) = CreateTempDir(
            ("MyAwesomeApp.sln", "// sln content"),
            ("package.json", "{\"name\":\"nodeapp\"}")); // ignored — sln takes priority
        try
        {
            Assert.Equal("MyAwesomeApp", IndexingPipeline.DetectProjectName(dir));
        }
        finally
        {
            cleanup();
        }
    }

    [Fact]
    public void DetectProjectName_SolutionXFile_ReturnsSlnxName()
    {
        var (dir, cleanup) = CreateTempDir(("CortexPlexus.slnx", "<Solution/>"));
        try
        {
            Assert.Equal("CortexPlexus", IndexingPipeline.DetectProjectName(dir));
        }
        finally
        {
            cleanup();
        }
    }

    // === #86 partial: DetectProjectName — package.json (Node.js) ===
    [Fact]
    public void DetectProjectName_PackageJson_ReturnsNameField()
    {
        var (dir, cleanup) = CreateTempDir(
            ("package.json", "{\"name\":\"my-node-app\",\"version\":\"1.0.0\"}"));
        try
        {
            Assert.Equal("my-node-app", IndexingPipeline.DetectProjectName(dir));
        }
        finally
        {
            cleanup();
        }
    }

    [Fact]
    public void DetectProjectName_ScopedPackage_StripsOrgScope()
    {
        // Mục đích: "@org/my-app" → "my-app" (strip scope).
        var (dir, cleanup) = CreateTempDir(
            ("package.json", "{\"name\":\"@mycompany/awesome-lib\"}"));
        try
        {
            Assert.Equal("awesome-lib", IndexingPipeline.DetectProjectName(dir));
        }
        finally
        {
            cleanup();
        }
    }

    // === #86 partial: DetectProjectName — pyproject.toml (Python) ===
    [Fact]
    public void DetectProjectName_PyprojectToml_ReturnsName()
    {
        var (dir, cleanup) = CreateTempDir(
            ("pyproject.toml", "[project]\nname = \"my-python-lib\"\nversion = \"0.1.0\""));
        try
        {
            Assert.Equal("my-python-lib", IndexingPipeline.DetectProjectName(dir));
        }
        finally
        {
            cleanup();
        }
    }

    [Fact]
    public void DetectProjectName_SingleCsproj_ReturnsCsprojName()
    {
        var (dir, cleanup) = CreateTempDir(
            ("MyLib.csproj", "<Project/>"));
        try
        {
            Assert.Equal("MyLib", IndexingPipeline.DetectProjectName(dir));
        }
        finally
        {
            cleanup();
        }
    }

    [Fact]
    public void DetectProjectName_MultipleCsprojNoSln_FallsBackToFolderName()
    {
        // Mục đích: Nhiều .csproj → không dùng csproj, fall back folder name.
        var (dir, cleanup) = CreateTempDir(
            ("ProjA.csproj", "<Project/>"),
            ("ProjB.csproj", "<Project/>"));
        try
        {
            var expected = Path.GetFileName(dir);
            Assert.Equal(expected, IndexingPipeline.DetectProjectName(dir));
        }
        finally
        {
            cleanup();
        }
    }

    [Fact]
    public void DetectProjectName_EmptyDirectory_ReturnsFolderName()
    {
        var (dir, cleanup) = CreateTempDir();
        try
        {
            var expected = Path.GetFileName(dir);
            Assert.Equal(expected, IndexingPipeline.DetectProjectName(dir));
        }
        finally
        {
            cleanup();
        }
    }

    [Fact]
    public void DetectProjectName_NonExistentPath_ReturnsBasename()
    {
        // Mục đích: Path không tồn tại → fallback Path.GetFileName (không crash).
        var ghostPath = Path.Combine(Path.GetTempPath(), $"ghost-{Guid.NewGuid():N}");
        var result = IndexingPipeline.DetectProjectName(ghostPath);
        Assert.NotNull(result);
        Assert.Equal(Path.GetFileName(ghostPath), result);
    }

    [Fact]
    public void DetectProjectName_TrailingSlash_Normalized()
    {
        // Mục đích: Path có trailing slash không làm kết quả thay đổi.
        var (dir, cleanup) = CreateTempDir(("App.sln", "// sln"));
        try
        {
            var withSlash = dir + Path.DirectorySeparatorChar;

            Assert.Equal("App", IndexingPipeline.DetectProjectName(dir));
            Assert.Equal("App", IndexingPipeline.DetectProjectName(withSlash));
        }
        finally
        {
            cleanup();
        }
    }

    // === #88: FindAllSolutionsAndProjects — prefer .sln over .csproj ===
    [Fact]
    public void FindAllSolutionsAndProjects_SolutionPresent_IgnoresCsproj()
    {
        var (dir, cleanup) = CreateTempDir(
            ("App.sln", "// sln"),
            ("src/App/App.csproj", "// csproj"),
            ("src/Lib/Lib.csproj", "// csproj"));
        try
        {
            var result = IndexingPipeline.FindAllSolutionsAndProjects(dir);

            Assert.Single(result);
            Assert.EndsWith("App.sln", result[0]);
        }
        finally
        {
            cleanup();
        }
    }

    [Fact]
    public void FindAllSolutionsAndProjects_NoSln_ReturnsAllCsproj()
    {
        var (dir, cleanup) = CreateTempDir(
            ("src/A/A.csproj", "// a"),
            ("src/B/B.csproj", "// b"));
        try
        {
            var result = IndexingPipeline.FindAllSolutionsAndProjects(dir);

            Assert.Equal(2, result.Count);
            Assert.All(result, r => Assert.EndsWith(".csproj", r));
        }
        finally
        {
            cleanup();
        }
    }

    [Fact]
    public void FindAllSolutionsAndProjects_ExcludesBinObjCopies()
    {
        // Mục đích: .csproj trong bin/ hoặc obj/ bị filter (copies của build).
        var (dir, cleanup) = CreateTempDir(
            ("src/App.csproj", "// real"),
            ("src/bin/Debug/App.csproj", "// build copy"),
            ("src/obj/Debug/App.csproj", "// obj copy"));
        try
        {
            var result = IndexingPipeline.FindAllSolutionsAndProjects(dir);

            Assert.Single(result);
            Assert.Contains("src", result[0]);
            Assert.DoesNotContain(result, p => p.Contains("bin"));
            Assert.DoesNotContain(result, p => p.Contains("obj"));
        }
        finally
        {
            cleanup();
        }
    }

    [Fact]
    public void FindAllSolutionsAndProjects_DirectSlnFile_ReturnsIt()
    {
        // Mục đích: Pass .sln file trực tiếp → trả về [file] (không scan).
        var (dir, cleanup) = CreateTempDir(("My.sln", "// sln"));
        try
        {
            var slnFile = Path.Combine(dir, "My.sln");
            var result = IndexingPipeline.FindAllSolutionsAndProjects(slnFile);

            Assert.Single(result);
            Assert.Equal(slnFile, result[0]);
        }
        finally
        {
            cleanup();
        }
    }

    [Fact]
    public void FindAllSolutionsAndProjects_NonExistentDirectory_ReturnsEmpty()
    {
        var ghost = Path.Combine(Path.GetTempPath(), $"ghost-{Guid.NewGuid():N}");
        var result = IndexingPipeline.FindAllSolutionsAndProjects(ghost);
        Assert.Empty(result);
    }

    [Fact]
    public void FindAllSolutionsAndProjects_DirectoryNoProjects_ReturnsEmpty()
    {
        var (dir, cleanup) = CreateTempDir(
            ("README.md", "# test"),
            ("src/index.ts", "// ts"));
        try
        {
            var result = IndexingPipeline.FindAllSolutionsAndProjects(dir);
            Assert.Empty(result);
        }
        finally
        {
            cleanup();
        }
    }

    // === #91 partial: IsExcludedPath ===
    // Lưu ý: IsExcludedPath yêu cầu separator 2 bên tên folder
    // (`{sep}bin{sep}`, không phải chỉ `bin{sep}`). Cần leading separator/path prefix.
    [Theory]
    [InlineData("/root/src/bin/Debug/file.cs", true)]
    [InlineData("/root/project/obj/Release/file.cs", true)]
    [InlineData("/root/node_modules/lib/index.ts", true)]
    [InlineData("/root/src/Services/Service.cs", false)]
    [InlineData("/root/my-bin-folder/file.cs", false)] // "bin" trong tên folder, không phải bin dir
    public void IsExcludedPath_Classification(string relativePath, bool expected)
    {
        // Convert sang native separator để test chạy đúng trên Windows vs Linux.
        var sep = Path.DirectorySeparatorChar;
        var path = relativePath.Replace('/', sep);
        Assert.Equal(expected, IndexingPipeline.IsExcludedPath(path));
    }

    [Fact]
    public void IsExcludedPath_PathWithoutLeadingSeparator_NotExcluded()
    {
        // Mục đích: Ghi nhận behavior hiện tại — nếu `bin` ở vị trí đầu path (không có
        // separator phía trước), IsExcludedPath trả FALSE. Đây là implementation detail
        // ít quan trọng trong thực tế vì `Directory.GetFiles()` luôn trả absolute/prefixed paths.
        // Test này tồn tại để catch regression nếu logic đổi (potential improvement cho tương lai).
        Assert.False(IndexingPipeline.IsExcludedPath("bin/file.cs".Replace('/', Path.DirectorySeparatorChar)));
        Assert.False(IndexingPipeline.IsExcludedPath("node_modules/lib.ts".Replace('/', Path.DirectorySeparatorChar)));
    }
}
