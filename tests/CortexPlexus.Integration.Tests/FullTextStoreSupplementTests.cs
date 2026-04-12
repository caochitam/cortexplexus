using CortexPlexus.Core.Models;
using CortexPlexus.Graph;
using Npgsql;

namespace CortexPlexus.Integration.Tests;

/// <summary>
/// Edge-case tests cho FullTextStore (bổ sung FullTextStoreIntegrationTests).
///
/// Phạm vi: TEST-PLAN.md #24, #25, #26, #27, #28
///
/// Tập trung vào fallback strategy chain (AND → OR → ILIKE) và Unicode handling.
/// </summary>
[Collection("Postgres")]
public sealed class FullTextStoreSupplementTests(PostgresFixture fixture)
{
    private static async Task SeedSymbolAsync(
        NpgsqlDataSource ds,
        string fqn,
        string name,
        Guid repoId,
        string signature = "",
        string kind = "method")
    {
        await using var conn = await ds.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO code_symbols (fqn, name, kind, signature, file_path, start_line, end_line, repo_id)
            VALUES (@fqn, @name, @kind, @sig, @fp, 1, 10, @repo)
            ON CONFLICT (fqn) DO NOTHING
            """;
        cmd.Parameters.AddWithValue("@fqn", fqn);
        cmd.Parameters.AddWithValue("@name", name);
        cmd.Parameters.AddWithValue("@kind", kind);
        cmd.Parameters.AddWithValue("@sig", signature);
        cmd.Parameters.AddWithValue("@fp", $"/repo/{name}.cs");
        cmd.Parameters.AddWithValue("@repo", repoId);
        await cmd.ExecuteNonQueryAsync();
    }

    // === #24 + #25: Fallback chain — AND fails, OR succeeds ===
    [Fact]
    public async Task Search_MultiTermQuery_ORfallbackReturnsAnyMatch()
    {
        // Mục đích: Query "payment user" — không symbol nào có cả 2 words.
        // AND strategy trả 0 → fallback sang OR → tìm symbol có ít nhất 1 word.
        await using var ds = fixture.CreateDataSource();
        var repoId = await fixture.SeedRepositoryAsync(ds, "fallback-test", $"/fallback/{Guid.NewGuid():N}");
        var store = new FullTextStore(ds);

        // Symbol chỉ có "payment"
        await SeedSymbolAsync(ds, "App.PaymentService", "PaymentService", repoId);
        // Symbol chỉ có "user"
        await SeedSymbolAsync(ds, "App.UserController", "UserController", repoId);

        // Query 2 terms — AND không có match, OR phải tìm được cả 2.
        var results = await store.SearchAsync("payment user", limit: 10, repoId: repoId);

        Assert.NotEmpty(results);
        // Phải match ít nhất 1 trong 2 symbols.
        Assert.Contains(results, r => r.Fqn.Contains("Payment") || r.Fqn.Contains("User"));
    }

    // === #25b: AND succeeds — không fallback ===
    [Fact]
    public async Task Search_SingleTermMatchingName_ReturnsDirectly()
    {
        // Mục đích: Single term match trực tiếp ở AND strategy — không cần fallback.
        await using var ds = fixture.CreateDataSource();
        var repoId = await fixture.SeedRepositoryAsync(ds, "direct-test", $"/direct/{Guid.NewGuid():N}");
        var store = new FullTextStore(ds);

        await SeedSymbolAsync(ds, "App.OrderProcessor", "OrderProcessor", repoId);

        var results = await store.SearchAsync("order", limit: 10, repoId: repoId);

        Assert.NotEmpty(results);
        Assert.Contains(results, r => r.Name == "OrderProcessor");
    }

    // === #26: Search_UnicodeQuery_NoSqlError ===
    [Fact]
    public async Task Search_VietnameseQuery_DoesNotCrash()
    {
        // Mục đích: Query tiếng Việt không crash PG parser.
        // Search không cần phải match — chỉ cần verify không throw.
        await using var ds = fixture.CreateDataSource();
        var repoId = await fixture.SeedRepositoryAsync(ds, "unicode-test", $"/unicode/{Guid.NewGuid():N}");
        var store = new FullTextStore(ds);

        await SeedSymbolAsync(ds, "App.XuLyThanhToan", "XuLyThanhToan", repoId);

        // Tiếng Việt có dấu — chỉ verify không throw error.
        var results = await store.SearchAsync("xử lý thanh toán", limit: 10, repoId: repoId);

        Assert.NotNull(results);
        // Không cần assert match — tsvector 'english' không xử lý tiếng Việt tốt,
        // mà quan trọng là KHÔNG crash vì ký tự đặc biệt.
    }

    [Fact]
    public async Task Search_ChineseQuery_DoesNotCrash()
    {
        await using var ds = fixture.CreateDataSource();
        var repoId = await fixture.SeedRepositoryAsync(ds, "cjk-test", $"/cjk/{Guid.NewGuid():N}");
        var store = new FullTextStore(ds);

        var results = await store.SearchAsync("支付服务", limit: 10, repoId: repoId);

        Assert.NotNull(results); // không throw
    }

    // === #27: Search_VeryLongQuery_Truncated / NoError ===
    [Fact]
    public async Task Search_VeryLongQuery_DoesNotCrash()
    {
        // Mục đích: Query 2000+ chars không crash PG parser.
        await using var ds = fixture.CreateDataSource();
        var repoId = await fixture.SeedRepositoryAsync(ds, "long-test", $"/long/{Guid.NewGuid():N}");
        var store = new FullTextStore(ds);

        await SeedSymbolAsync(ds, "App.Service", "Service", repoId);

        // Query 2000 words (~10000 chars).
        var longQuery = string.Join(" ", Enumerable.Repeat("word", 2000));

        var results = await store.SearchAsync(longQuery, limit: 10, repoId: repoId);

        // Không crash, return ok (có thể empty hoặc có match, không quan trọng).
        Assert.NotNull(results);
    }

    // === #28: Empty/whitespace query handling ===
    [Fact]
    public async Task Search_WhitespaceQuery_ReturnsEmptyImmediately()
    {
        // Mục đích: query rỗng/whitespace trả empty ngay (không query DB).
        await using var ds = fixture.CreateDataSource();
        var store = new FullTextStore(ds);

        var results1 = await store.SearchAsync("", limit: 10);
        var results2 = await store.SearchAsync("   ", limit: 10);

        Assert.Empty(results1);
        Assert.Empty(results2);
    }

    // === Bonus: Special chars sanitization ===
    [Fact]
    public async Task Search_SpecialCharsInQuery_DoesNotCrash()
    {
        // Mục đích: tsquery special chars (|, &, !, :, (, )) được sanitize bởi
        // SanitizeTsQueryTerm regex — không crash.
        await using var ds = fixture.CreateDataSource();
        var repoId = await fixture.SeedRepositoryAsync(ds, "special-test", $"/special/{Guid.NewGuid():N}");
        var store = new FullTextStore(ds);

        await SeedSymbolAsync(ds, "App.Service", "Service", repoId);

        // Query với ký tự đặc biệt của tsquery.
        var results = await store.SearchAsync("service & (broken | :)", limit: 10, repoId: repoId);

        Assert.NotNull(results);
        // Không crash là đủ — kết quả tuỳ thuộc vào fallback strategy.
    }
}
