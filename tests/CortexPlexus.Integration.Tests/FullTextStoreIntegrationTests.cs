using CortexPlexus.Core.Models;
using CortexPlexus.Graph;

namespace CortexPlexus.Integration.Tests;

[Collection("Postgres")]
public sealed class FullTextStoreIntegrationTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Search_FindsByName()
    {
        await using var ds = fixture.CreateDataSource();
        var repoId = await fixture.SeedRepositoryAsync(ds, "fts-name", "/fts-name");
        await SeedSymbols(ds, repoId);

        var store = new FullTextStore(ds);
        var results = await store.SearchAsync("PaymentService");

        Assert.NotEmpty(results);
        Assert.Contains(results, r => r.Fqn == "App.PaymentService");
    }

    [Fact]
    public async Task Search_NameWeightHigherThanSignature()
    {
        await using var ds = fixture.CreateDataSource();
        var repoId = await fixture.SeedRepositoryAsync(ds, "fts-weight", "/fts-weight");

        // Insert a class named "Invoice" and a method whose signature mentions "invoice"
        await InsertSymbol(ds, "App.Invoice", "Invoice", "class", null, repoId);
        await InsertSymbol(ds, "App.Billing.ProcessInvoice", "ProcessInvoice", "method",
            "Task ProcessInvoice(Invoice invoice)", repoId);

        var store = new FullTextStore(ds);
        var results = await store.SearchAsync("Invoice");

        Assert.True(results.Count >= 2);
        // The class named "Invoice" (weight A=1.0) should rank higher than
        // the method with "invoice" only in signature (weight C=0.2)
        Assert.Equal("App.Invoice", results[0].Fqn);
    }

    [Fact]
    public async Task Search_FiltersByRepoId()
    {
        await using var ds = fixture.CreateDataSource();
        var repo1 = await fixture.SeedRepositoryAsync(ds, "fts-r1", "/fts-r1");
        var repo2 = await fixture.SeedRepositoryAsync(ds, "fts-r2", "/fts-r2");

        await InsertSymbol(ds, "R1.OrderService", "OrderService", "class", null, repo1);
        await InsertSymbol(ds, "R2.OrderService", "OrderService", "class", null, repo2);

        var store = new FullTextStore(ds);
        var results = await store.SearchAsync("OrderService", repoId: repo1);

        Assert.Single(results);
        Assert.Equal("R1.OrderService", results[0].Fqn);
    }

    [Fact]
    public async Task Search_FiltersByKind()
    {
        await using var ds = fixture.CreateDataSource();
        var repoId = await fixture.SeedRepositoryAsync(ds, "fts-kind", "/fts-kind");

        await InsertSymbol(ds, "App.UserService", "UserService", "class", null, repoId);
        await InsertSymbol(ds, "App.UserService.GetUser", "GetUser", "method",
            "Task<User> GetUser(int id)", repoId);

        var store = new FullTextStore(ds);
        var results = await store.SearchAsync("User", kind: "method");

        Assert.All(results, r => Assert.Equal("method", r.Kind));
    }

    [Fact]
    public async Task Search_EmptyQuery_ReturnsEmpty()
    {
        await using var ds = fixture.CreateDataSource();
        var store = new FullTextStore(ds);

        var results = await store.SearchAsync("");
        Assert.Empty(results);

        results = await store.SearchAsync("   ");
        Assert.Empty(results);
    }

    [Fact]
    public async Task Search_NoMatch_ReturnsEmpty()
    {
        await using var ds = fixture.CreateDataSource();
        var store = new FullTextStore(ds);

        var results = await store.SearchAsync("xyznonexistentqueryxyz123");
        Assert.Empty(results);
    }

    // --- Helpers ---

    private static async Task SeedSymbols(Npgsql.NpgsqlDataSource ds, Guid repoId)
    {
        await InsertSymbol(ds, "App.PaymentService", "PaymentService", "class",
            "public class PaymentService", repoId);
        await InsertSymbol(ds, "App.OrderService", "OrderService", "class",
            "public class OrderService", repoId);
        await InsertSymbol(ds, "App.PaymentService.Process", "Process", "method",
            "Task<Result> Process(Order order)", repoId);
    }

    private static async Task InsertSymbol(
        Npgsql.NpgsqlDataSource ds, string fqn, string name, string kind, string? signature, Guid repoId)
    {
        await using var conn = await ds.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO code_symbols (fqn, name, kind, signature, file_path, start_line, end_line, repo_id)
            VALUES (@fqn, @name, @kind, @sig, @fp, 1, 10, @rid)
            ON CONFLICT (fqn) DO NOTHING
            """;
        cmd.Parameters.AddWithValue("@fqn", fqn);
        cmd.Parameters.AddWithValue("@name", name);
        cmd.Parameters.AddWithValue("@kind", kind);
        cmd.Parameters.AddWithValue("@sig", (object?)signature ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@fp", $"{name}.cs");
        cmd.Parameters.AddWithValue("@rid", repoId);
        await cmd.ExecuteNonQueryAsync();
    }
}
