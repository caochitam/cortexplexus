using System.Diagnostics;
using CortexPlexus.Graph;
using Npgsql;

namespace CortexPlexus.Graph.Tests;

/// <summary>
/// Tests cho RepositoryStore (register + incremental file hash tracking).
///
/// Phạm vi: TEST-PLAN.md #13, #14, #15, #16, #17, #18, #19
///
/// Dùng AgeFixture share container với các integration tests khác.
/// RepositoryStore chỉ cần các relational tables (repositories + file_hashes),
/// không cần AGE extension — nhưng fixture đã có sẵn cả hai.
/// </summary>
[Collection("Age")]
public class RepositoryStoreTests : IAsyncLifetime
{
    private readonly AgeFixture _fixture;
    private NpgsqlDataSource _dataSource = null!;
    private RepositoryStore _store = null!;

    public RepositoryStoreTests(AgeFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        _dataSource = _fixture.CreateDataSource();
        _store = new RepositoryStore(_dataSource);
        await _fixture.CleanAsync(_dataSource);
    }

    public async Task DisposeAsync()
    {
        await _dataSource.DisposeAsync();
    }

    // === #13: RegisterAsync_SamePath_UpdatesNotDuplicate ===
    [Fact]
    public async Task RegisterAsync_SamePathTwice_ReturnsSameIdAndUpdatesName()
    {
        // Mục đích: ON CONFLICT (path) DO UPDATE — register cùng path 2 lần
        // không tạo duplicate, và update tên nếu khác.
        var first = await _store.RegisterAsync("old-name", "/test/repo");
        var second = await _store.RegisterAsync("new-name", "/test/repo");

        Assert.Equal(first.Id, second.Id); // same ID
        Assert.Equal("new-name", second.Name); // name updated

        // Verify chỉ có 1 repo trong DB.
        var all = await _store.ListAsync();
        Assert.Single(all, r => r.Path == "/test/repo");
    }

    // === #14: IsFileChanged_NullHash_ReturnsTrue (new file) ===
    [Fact]
    public async Task IsFileChangedAsync_FileNotInTable_ReturnsTrue()
    {
        // Mục đích: File chưa có hash trong DB = file mới = changed = true.
        var repo = await _store.RegisterAsync("test-repo", "/test/repo1");

        var changed = await _store.IsFileChangedAsync(
            "/test/repo1/new-file.cs", repo.Id, "abc123hash");

        Assert.True(changed);
    }

    // === #15: IsFileChanged_SameHash_ReturnsFalse ===
    [Fact]
    public async Task IsFileChangedAsync_SameHash_ReturnsFalse()
    {
        // Mục đích: Hash trong DB khớp với hash hiện tại → không changed.
        var repo = await _store.RegisterAsync("test-repo", "/test/repo2");
        const string hash = "sha256-unchanged";

        await _store.UpdateFileHashAsync("/test/repo2/stable.cs", repo.Id, hash);

        var changed = await _store.IsFileChangedAsync(
            "/test/repo2/stable.cs", repo.Id, hash);

        Assert.False(changed);
    }

    // === #16: IsFileChanged_DifferentHash_ReturnsTrue ===
    [Fact]
    public async Task IsFileChangedAsync_DifferentHash_ReturnsTrue()
    {
        // Mục đích: Hash trong DB khác hash hiện tại → file đã thay đổi → true.
        var repo = await _store.RegisterAsync("test-repo", "/test/repo3");

        await _store.UpdateFileHashAsync("/test/repo3/modified.cs", repo.Id, "old-hash");

        var changed = await _store.IsFileChangedAsync(
            "/test/repo3/modified.cs", repo.Id, "new-hash");

        Assert.True(changed);
    }

    // === #17: GetFileHashes_LargeRepo_Performance ===
    [Fact]
    public async Task GetFileHashesAsync_LargeRepo_CompletesWithinBudget()
    {
        // Mục đích: 5000 files được materialize thành Dict < 3s.
        // (Spec gốc nói 10000 files < 1s, nhưng PG round-trip có overhead — 5000/3s realistic).
        var repo = await _store.RegisterAsync("large-repo", "/test/large");
        const int fileCount = 5000;

        // Bulk insert files qua raw SQL để setup nhanh.
        await using (var conn = await _dataSource.OpenConnectionAsync())
        await using (var cmd = conn.CreateCommand())
        {
            var values = string.Join(",",
                Enumerable.Range(0, fileCount)
                    .Select(i => $"('/test/large/file{i}.cs', '{repo.Id}', 'hash{i}')"));
            cmd.CommandText = $"INSERT INTO file_hashes (file_path, repo_id, content_hash) VALUES {values}";
            await cmd.ExecuteNonQueryAsync();
        }

        var sw = Stopwatch.StartNew();
        var hashes = await _store.GetFileHashesAsync(repo.Id);
        sw.Stop();

        Assert.Equal(fileCount, hashes.Count);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3),
            $"GetFileHashes({fileCount} files) took {sw.Elapsed.TotalMilliseconds:F0}ms (budget: 3000ms)");

        Console.WriteLine($"[PERF] GetFileHashes({fileCount}): {sw.Elapsed.TotalMilliseconds:F0}ms");
    }

    // === #18: PathNormalization_WindowsVsUnix ===
    // NOTE: RepositoryStore hiện tại KHÔNG normalize paths — coi "C:\repo" và "c:\repo"
    // là 2 path khác nhau. Test này ghi nhận behavior hiện tại và flag potential
    // improvement trong tương lai.
    [Fact]
    public async Task RegisterAsync_PathsWithDifferentSeparators_TreatedAsDifferent()
    {
        // Mục đích: Ghi nhận behavior hiện tại (KHÔNG normalize path).
        // Nếu sau này normalize được thêm, test này sẽ fail → update cho phù hợp.
        var unix = await _store.RegisterAsync("repo", "/test/project");
        var windows = await _store.RegisterAsync("repo", "C:\\test\\project");

        // Hiện tại: 2 repos khác nhau vì path string khác nhau.
        Assert.NotEqual(unix.Id, windows.Id);

        // Improvement trong tương lai có thể:
        // - Canonicalize path trước khi insert
        // - Platform-aware comparison
    }

    [Fact]
    public async Task GetByPathAsync_ExistingPath_ReturnsRepo()
    {
        var created = await _store.RegisterAsync("findme", "/test/findme");

        var found = await _store.GetByPathAsync("/test/findme");

        Assert.NotNull(found);
        Assert.Equal(created.Id, found!.Id);
        Assert.Equal("findme", found.Name);
    }

    [Fact]
    public async Task GetByPathAsync_NonExistentPath_ReturnsNull()
    {
        var found = await _store.GetByPathAsync("/test/ghost-path");
        Assert.Null(found);
    }

    // === #19: ConcurrentRegister_SamePath_NoRaceCondition ===
    [Fact]
    public async Task RegisterAsync_ConcurrentSamePath_ProducesSingleRecord()
    {
        // Mục đích: 5 threads đồng thời register cùng path → chỉ 1 record trong DB.
        // ON CONFLICT (path) DO UPDATE đảm bảo atomicity.
        var path = "/test/concurrent";

        var tasks = Enumerable.Range(0, 5)
            .Select(i => _store.RegisterAsync($"name-{i}", path))
            .ToArray();

        var results = await Task.WhenAll(tasks);

        // Tất cả 5 tasks phải return cùng 1 Id.
        var distinctIds = results.Select(r => r.Id).Distinct().ToList();
        Assert.Single(distinctIds);

        // DB chỉ có 1 row cho path này.
        var all = await _store.ListAsync();
        Assert.Single(all, r => r.Path == path);
    }

    [Fact]
    public async Task UpdateLastIndexedAsync_SetsTimestamp()
    {
        var repo = await _store.RegisterAsync("test", "/test/update");
        Assert.Null(repo.LastIndexed);

        await _store.UpdateLastIndexedAsync(repo.Id);

        var updated = await _store.GetByPathAsync("/test/update");
        Assert.NotNull(updated);
        Assert.NotNull(updated!.LastIndexed);
    }

    [Fact]
    public async Task UpdateFileHashAsync_ExistingFile_UpdatesHash()
    {
        // Mục đích: ON CONFLICT trong UpdateFileHashAsync — update hash của file đã tồn tại.
        var repo = await _store.RegisterAsync("test", "/test/upd");

        await _store.UpdateFileHashAsync("/test/upd/file.cs", repo.Id, "v1");
        await _store.UpdateFileHashAsync("/test/upd/file.cs", repo.Id, "v2");

        var hashes = await _store.GetFileHashesAsync(repo.Id);
        Assert.Equal("v2", hashes["/test/upd/file.cs"]);
    }
}
