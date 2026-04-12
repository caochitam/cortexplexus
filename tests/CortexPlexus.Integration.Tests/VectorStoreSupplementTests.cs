using CortexPlexus.Core.Models;
using CortexPlexus.Graph;
using Microsoft.Extensions.Logging.Abstractions;

namespace CortexPlexus.Integration.Tests;

/// <summary>
/// Edge-case tests cho VectorStore (bổ sung cho VectorStoreIntegrationTests).
///
/// Phạm vi: TEST-PLAN.md #20, #21, #22, #23
/// </summary>
[Collection("Postgres")]
public sealed class VectorStoreSupplementTests(PostgresFixture fixture)
{
    private static ClassInfo MakeClass(string fqn, string name, Guid repoId) => new()
    {
        Fqn = fqn,
        Name = name,
        Kind = "class",
        FilePath = $"{name}.cs",
        StartLine = 1,
        EndLine = 10,
        RepoId = repoId
    };

    private static float[] CreateEmbedding(float seed)
    {
        var emb = new float[768];
        for (int i = 0; i < 768; i++)
            emb[i] = MathF.Sin(seed * (i + 1));
        return emb;
    }

    // === #20: Upsert_NullEmbedding_SkipsGracefully ===
    [Fact]
    public async Task UpsertAsync_SymbolWithoutEmbedding_InsertsWithNullEmbedding()
    {
        // Mục đích: Symbol không có embedding trong dict → record vẫn được insert
        // với embedding = NULL, không crash. Search vector sẽ không trả về
        // (vì SearchAsync có "AND embedding IS NOT NULL").
        await using var ds = fixture.CreateDataSource();
        var repoId = await fixture.SeedRepositoryAsync(ds, "nullemb-test", $"/test/{Guid.NewGuid():N}");
        var store = new VectorStore(ds, NullLogger<VectorStore>.Instance);

        var prefix = $"NullEmb{Guid.NewGuid():N}.";
        var withEmb = MakeClass($"{prefix}WithEmb", "WithEmb", repoId);
        var withoutEmb = MakeClass($"{prefix}WithoutEmb", "WithoutEmb", repoId);

        var embeddings = new Dictionary<string, float[]>
        {
            [$"{prefix}WithEmb"] = CreateEmbedding(0.3f),
            // WithoutEmb intentionally missing
        };

        try
        {
            // Must not throw
            await store.UpsertAsync([withEmb, withoutEmb], embeddings);

            // Vector search: chỉ trả symbol có embedding.
            var results = await store.SearchAsync(CreateEmbedding(0.3f), limit: 10, repoId: repoId);
            Assert.Single(results, r => r.Fqn == $"{prefix}WithEmb");
            Assert.DoesNotContain(results, r => r.Fqn == $"{prefix}WithoutEmb");
        }
        finally
        {
            await store.DeleteByRepoAsync(repoId);
        }
    }

    // === #21: Upsert_LargeBatch_ChunksCorrectly ===
    [Fact]
    public async Task UpsertAsync_500Symbols_ChunksAndInsertsAll()
    {
        // Mục đích: 500 symbols > BatchSize (200) → chia thành 3 batches, tất cả inserted.
        // Dùng FQN prefix unique để không ô nhiễm các tests khác chia sẻ cùng Postgres container.
        await using var ds = fixture.CreateDataSource();
        var repoId = await fixture.SeedRepositoryAsync(ds, "bigbatch-test", $"/test/{Guid.NewGuid():N}");
        var store = new VectorStore(ds, NullLogger<VectorStore>.Instance);

        var prefix = $"BigBatch{Guid.NewGuid():N}.";
        var symbols = Enumerable.Range(0, 500)
            .Select(i => MakeClass($"{prefix}Class{i}", $"Class{i}", repoId) as CodeSymbol)
            .ToList();

        var embedding = CreateEmbedding(0.5f);
        var embeddings = symbols.ToDictionary(s => s.Fqn, _ => embedding);

        try
        {
            await store.UpsertAsync(symbols, embeddings);

            // Verify: search trả về đúng 500 symbols cho repo này.
            var results = await store.SearchAsync(embedding, limit: 1000, repoId: repoId);
            Assert.Equal(500, results.Count);
        }
        finally
        {
            // Cleanup: xoá symbols khỏi DB để không ô nhiễm tests chia sẻ cùng Postgres container.
            await store.DeleteByRepoAsync(repoId);
        }
    }

    // === #22: Search_ZeroVectorQuery_DoesNotCrash ===
    [Fact]
    public async Task SearchAsync_ZeroVectorQuery_ReturnsWithoutError()
    {
        // Mục đích: Query embedding toàn 0 (degenerate case) không crash.
        // Cosine similarity với zero vector là undefined, nhưng pgvector handle ok
        // (trả NaN scores nhưng không throw).
        await using var ds = fixture.CreateDataSource();
        var repoId = await fixture.SeedRepositoryAsync(ds, "zero-test", $"/test/{Guid.NewGuid():N}");
        var store = new VectorStore(ds, NullLogger<VectorStore>.Instance);

        var prefix = $"Zero{Guid.NewGuid():N}.";
        var symbol = MakeClass($"{prefix}Service", "Service", repoId);
        try
        {
            await store.UpsertAsync([symbol],
                new Dictionary<string, float[]> { [$"{prefix}Service"] = CreateEmbedding(0.5f) });

            var zeroVector = new float[768]; // toàn 0

            // Must not throw — pgvector có thể trả NaN score nhưng không error.
            var results = await store.SearchAsync(zeroVector, limit: 5, repoId: repoId);

            Assert.NotNull(results);
            // Không cần assert score cụ thể — chỉ cần verify không crash.
        }
        finally
        {
            await store.DeleteByRepoAsync(repoId);
        }
    }

    // === #23: Search_ScoreNormalization_WithinBounds ===
    [Fact]
    public async Task SearchAsync_IdenticalVectors_ScoreCloseToOne()
    {
        // Mục đích: Query với chính vector của symbol → similarity ≈ 1.0
        // (verify score normalization: 1 - cosine_distance, distance = 0 → score = 1).
        await using var ds = fixture.CreateDataSource();
        var repoId = await fixture.SeedRepositoryAsync(ds, "score-test", $"/test/{Guid.NewGuid():N}");
        var store = new VectorStore(ds, NullLogger<VectorStore>.Instance);

        var prefix = $"Score{Guid.NewGuid():N}.";
        var targetEmb = CreateEmbedding(0.8f);
        var otherEmb = CreateEmbedding(-0.8f);

        try
        {
            await store.UpsertAsync(
                [MakeClass($"{prefix}Target", "Target", repoId), MakeClass($"{prefix}Other", "Other", repoId)],
                new Dictionary<string, float[]>
                {
                    [$"{prefix}Target"] = targetEmb,
                    [$"{prefix}Other"] = otherEmb,
                });

            var results = await store.SearchAsync(targetEmb, limit: 5, repoId: repoId);

            var target = results.First(r => r.Fqn == $"{prefix}Target");
            Assert.True(target.Score > 0.99,
                $"Identical vectors should have score ~1.0, got {target.Score}");

            // Other vector với opposite direction → score phải thấp hơn rõ rệt.
            var other = results.FirstOrDefault(r => r.Fqn == $"{prefix}Other");
            if (other is not null)
                Assert.True(other.Score < target.Score,
                    $"Opposite vectors should have lower score. Target={target.Score}, Other={other.Score}");
        }
        finally
        {
            await store.DeleteByRepoAsync(repoId);
        }
    }
}
