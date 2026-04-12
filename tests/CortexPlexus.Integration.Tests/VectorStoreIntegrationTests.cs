using CortexPlexus.Core.Models;
using CortexPlexus.Graph;
using Microsoft.Extensions.Logging.Abstractions;

namespace CortexPlexus.Integration.Tests;

[Collection("Postgres")]
public sealed class VectorStoreIntegrationTests(PostgresFixture fixture)
{
    [Fact]
    public async Task UpsertAndSearch_RoundTrip()
    {
        await using var ds = fixture.CreateDataSource();
        var repoId = await fixture.SeedRepositoryAsync(ds);
        var store = new VectorStore(ds, NullLogger<VectorStore>.Instance);

        // Create a symbol with a known embedding
        var symbol = new ClassInfo
        {
            Fqn = "TestApp.PaymentService", Name = "PaymentService", Kind = "class",
            FilePath = "PaymentService.cs", StartLine = 1, EndLine = 50, RepoId = repoId
        };

        // Generate a deterministic embedding (768-dim)
        var embedding = CreateEmbedding(0.5f);
        var embeddings = new Dictionary<string, float[]> { ["TestApp.PaymentService"] = embedding };

        await store.UpsertAsync([symbol], embeddings);

        // Search with same embedding — should find our symbol
        var results = await store.SearchAsync(embedding, limit: 5, repoId: repoId);

        Assert.NotEmpty(results);
        Assert.Equal("TestApp.PaymentService", results[0].Fqn);
        Assert.Equal("PaymentService", results[0].Name);
        Assert.Equal("class", results[0].Kind);
        Assert.True(results[0].Score > 0.99, $"Expected high similarity, got {results[0].Score}");
    }

    [Fact]
    public async Task Search_FiltersByRepoId()
    {
        await using var ds = fixture.CreateDataSource();
        var repo1 = await fixture.SeedRepositoryAsync(ds, "repo1", "/repo1");
        var repo2 = await fixture.SeedRepositoryAsync(ds, "repo2", "/repo2");
        var store = new VectorStore(ds, NullLogger<VectorStore>.Instance);

        var symbol1 = MakeClass("Repo1.ClassA", "ClassA", repo1);
        var symbol2 = MakeClass("Repo2.ClassB", "ClassB", repo2);

        var emb = CreateEmbedding(0.3f);
        await store.UpsertAsync([symbol1], new Dictionary<string, float[]> { ["Repo1.ClassA"] = emb });
        await store.UpsertAsync([symbol2], new Dictionary<string, float[]> { ["Repo2.ClassB"] = emb });

        // Search only repo1
        var results = await store.SearchAsync(emb, limit: 10, repoId: repo1);

        Assert.All(results, r => Assert.Equal("ClassA", r.Name));
    }

    [Fact]
    public async Task Search_FiltersByKind()
    {
        await using var ds = fixture.CreateDataSource();
        var repoId = await fixture.SeedRepositoryAsync(ds, "kind-test", "/kind-test");
        var store = new VectorStore(ds, NullLogger<VectorStore>.Instance);

        var classSymbol = MakeClass("App.MyClass", "MyClass", repoId);
        var methodSymbol = new MethodInfo
        {
            Fqn = "App.MyClass.DoWork", Name = "DoWork", Kind = "method",
            FilePath = "MyClass.cs", StartLine = 5, EndLine = 10, RepoId = repoId,
            Signature = "void DoWork()", ContainingTypeFqn = "App.MyClass"
        };

        var emb = CreateEmbedding(0.7f);
        await store.UpsertAsync([classSymbol, methodSymbol], new Dictionary<string, float[]>
        {
            ["App.MyClass"] = emb,
            ["App.MyClass.DoWork"] = emb
        });

        var results = await store.SearchAsync(emb, limit: 10, kind: "method");

        Assert.Single(results);
        Assert.Equal("App.MyClass.DoWork", results[0].Fqn);
    }

    [Fact]
    public async Task Upsert_IsIdempotent()
    {
        await using var ds = fixture.CreateDataSource();
        var repoId = await fixture.SeedRepositoryAsync(ds, "idempotent", "/idempotent");
        var store = new VectorStore(ds, NullLogger<VectorStore>.Instance);

        var symbol = MakeClass("Idem.Service", "Service", repoId);
        var emb = CreateEmbedding(0.1f);
        var dict = new Dictionary<string, float[]> { ["Idem.Service"] = emb };

        // Upsert twice — should not throw
        await store.UpsertAsync([symbol], dict);
        await store.UpsertAsync([symbol], dict);

        var results = await store.SearchAsync(emb, limit: 10, repoId: repoId);
        // Should have exactly 1 result, not 2
        Assert.Single(results, r => r.Fqn == "Idem.Service");
    }

    [Fact]
    public async Task DeleteByRepo_RemovesAllSymbols()
    {
        await using var ds = fixture.CreateDataSource();
        var repoId = await fixture.SeedRepositoryAsync(ds, "del-test", "/del-test");
        var store = new VectorStore(ds, NullLogger<VectorStore>.Instance);

        var emb = CreateEmbedding(0.9f);
        await store.UpsertAsync(
            [MakeClass("Del.A", "A", repoId), MakeClass("Del.B", "B", repoId)],
            new Dictionary<string, float[]> { ["Del.A"] = emb, ["Del.B"] = emb });

        await store.DeleteByRepoAsync(repoId);

        var results = await store.SearchAsync(emb, limit: 10, repoId: repoId);
        Assert.Empty(results);
    }

    // === R18: HNSW bulk-load path ===

    [Fact]
    public async Task BulkLoad_Over500Symbols_DropsAndRecreatesHnsw()
    {
        // Purpose: verify that upserting ≥ BulkLoadThreshold (500) vectors triggers
        // the drop → insert → recreate path. We can't easily hook the drop/recreate
        // events, but we can assert end-to-end invariants:
        //   1. All 600 symbols are upserted and searchable (→ index is live afterward)
        //   2. The HNSW index exists post-upsert (→ recreate succeeded)
        //   3. Search returns the correct top hit (→ index is queryable, not missing)
        await using var ds = fixture.CreateDataSource();
        var repoId = await fixture.SeedRepositoryAsync(ds, "bulk-load", "/bulk");
        var store = new VectorStore(ds, NullLogger<VectorStore>.Instance);

        const int count = 600;
        var symbols = new List<CodeSymbol>(count);
        var embeddings = new Dictionary<string, float[]>(count);
        for (var i = 0; i < count; i++)
        {
            var fqn = $"Bulk.Class{i}";
            symbols.Add(MakeClass(fqn, $"Class{i}", repoId));
            // Distinct embeddings so search has something to rank
            embeddings[fqn] = CreateEmbedding(0.01f * (i + 1));
        }

        await store.UpsertAsync(symbols, embeddings);

        // Post-condition 1: HNSW index exists
        await using var conn = await ds.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT COUNT(*) FROM pg_indexes " +
            "WHERE tablename = 'code_symbols' AND indexname = 'idx_symbols_embedding'";
        var indexCount = Convert.ToInt64((await cmd.ExecuteScalarAsync())!);
        Assert.Equal(1L, indexCount);

        // Post-condition 2: Table contains all 600 + index is queryable (search uses <=> which needs HNSW for ANN)
        var targetEmb = CreateEmbedding(0.01f * 300); // match Class299
        var results = await store.SearchAsync(targetEmb, limit: 5, repoId: repoId);

        Assert.NotEmpty(results);
        Assert.Equal("Bulk.Class299", results[0].Fqn);
        // High-similarity match (cosine normalized)
        Assert.True(results[0].Score > 0.99, $"Expected near-1.0 similarity, got {results[0].Score}");
    }

    [Fact]
    public async Task BulkLoad_Under500Symbols_KeepsHnswLive()
    {
        // Purpose: verify below-threshold path — HNSW stays intact, no drop/recreate.
        // Invariants: upsert works, index still exists, search works.
        await using var ds = fixture.CreateDataSource();
        var repoId = await fixture.SeedRepositoryAsync(ds, "small-upsert", "/small");
        var store = new VectorStore(ds, NullLogger<VectorStore>.Instance);

        const int count = 50; // well below 500 threshold
        var symbols = new List<CodeSymbol>(count);
        var embeddings = new Dictionary<string, float[]>(count);
        for (var i = 0; i < count; i++)
        {
            var fqn = $"Small.Class{i}";
            symbols.Add(MakeClass(fqn, $"Class{i}", repoId));
            embeddings[fqn] = CreateEmbedding(0.02f * (i + 1));
        }

        await store.UpsertAsync(symbols, embeddings);

        // HNSW index still present
        await using var conn = await ds.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT COUNT(*) FROM pg_indexes " +
            "WHERE tablename = 'code_symbols' AND indexname = 'idx_symbols_embedding'";
        var indexCount = Convert.ToInt64((await cmd.ExecuteScalarAsync())!);
        Assert.Equal(1L, indexCount);

        // Symbols are findable
        var target = CreateEmbedding(0.02f * 25); // Class24
        var results = await store.SearchAsync(target, limit: 3, repoId: repoId);
        Assert.NotEmpty(results);
        Assert.Equal("Small.Class24", results[0].Fqn);
    }

    [Fact]
    public async Task BulkLoad_BelowThreshold_EvenWithManySymbolsButFewEmbeddings()
    {
        // Edge case: we pass 800 symbols but only 100 have embeddings.
        // Threshold is measured on embedded count (HNSW only indexes non-null),
        // so bulk-load should NOT trigger.
        await using var ds = fixture.CreateDataSource();
        var repoId = await fixture.SeedRepositoryAsync(ds, "sparse-emb", "/sparse");
        var store = new VectorStore(ds, NullLogger<VectorStore>.Instance);

        var symbols = new List<CodeSymbol>();
        var embeddings = new Dictionary<string, float[]>();
        for (var i = 0; i < 800; i++)
        {
            var fqn = $"Sparse.Class{i}";
            symbols.Add(MakeClass(fqn, $"Class{i}", repoId));
            if (i < 100)
                embeddings[fqn] = CreateEmbedding(0.03f * (i + 1));
        }

        // Should complete without dropping index. Only correctness matters in unit test;
        // timing validation comes from the BENCHMARK measurement.
        await store.UpsertAsync(symbols, embeddings);

        await using var conn = await ds.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT COUNT(*) FROM pg_indexes " +
            "WHERE tablename = 'code_symbols' AND indexname = 'idx_symbols_embedding'";
        Assert.Equal(1L, Convert.ToInt64((await cmd.ExecuteScalarAsync())!));

        // Embedded symbols are searchable
        var target = CreateEmbedding(0.03f * 50); // Class49
        var results = await store.SearchAsync(target, limit: 3, repoId: repoId);
        Assert.NotEmpty(results);
        Assert.Equal("Sparse.Class49", results[0].Fqn);
    }

    // --- Helpers ---

    private static ClassInfo MakeClass(string fqn, string name, Guid repoId) => new()
    {
        Fqn = fqn, Name = name, Kind = "class", FilePath = $"{name}.cs",
        StartLine = 1, EndLine = 10, RepoId = repoId
    };

    private static float[] CreateEmbedding(float seed)
    {
        var emb = new float[768];
        for (int i = 0; i < 768; i++)
            emb[i] = MathF.Sin(seed * (i + 1));
        // Normalize
        var norm = MathF.Sqrt(emb.Sum(x => x * x));
        for (int i = 0; i < 768; i++) emb[i] /= norm;
        return emb;
    }
}
