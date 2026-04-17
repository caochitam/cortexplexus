using CortexPlexus.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace CortexPlexus.Memory.Tests;

[Collection("Memory")]
public sealed class MemoryDecayTests(MemoryFixture fixture) : IAsyncLifetime
{
    private readonly MemoryFixture _fixture = fixture;
    private AgentMemoryStore _store = null!;
    private NpgsqlDataSource _dataSource = null!;

    public async Task InitializeAsync()
    {
        _dataSource = _fixture.CreateDataSource();
        _store = new AgentMemoryStore(_dataSource, NullLogger<AgentMemoryStore>.Instance);
        await _store.InitializeSchemaAsync();
        await _fixture.CleanAsync(_dataSource);
    }

    public async Task DisposeAsync() => await _dataSource.DisposeAsync();

    /// <summary>
    /// Directly write a row with a back-dated last_accessed_at so we can exercise the
    /// decay SQL without sleeping. Uses the raw DataSource (not via Save) to bypass the
    /// API that auto-stamps last_accessed_at to now().
    /// </summary>
    private async Task SeedAgedAsync(
        string content, string? topic, double importance, DateTimeOffset lastAccessed,
        string scope = MemoryScope.Global, string? scopeId = null)
    {
        await using var conn = await _dataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO agent_memories (content, scope, scope_id, topic, importance,
                                        created_at, last_accessed_at)
            VALUES (@c, @s, @sid, @t, @i, @ts, @ts)
            """;
        cmd.Parameters.AddWithValue("c", content);
        cmd.Parameters.AddWithValue("s", scope);
        cmd.Parameters.AddWithValue("sid", (object?)scopeId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("t", (object?)topic ?? DBNull.Value);
        cmd.Parameters.AddWithValue("i", (float)importance);
        cmd.Parameters.AddWithValue("ts", lastAccessed);
        await cmd.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task RecallAsync_FiltersOutForgottenMemories()
    {
        var now = DateTimeOffset.UtcNow;
        await SeedAgedAsync("fresh", MemoryTopic.Note, 0.5, now);
        await SeedAgedAsync("aged-todo", MemoryTopic.Todo, 0.5, now.AddDays(-60));  // score < 0.1
        await SeedAgedAsync("aged-pref", MemoryTopic.Preference, 0.8, now.AddDays(-100));

        var hits = await _store.RecallAsync(
            queryEmbedding: null, scope: null, scopeId: null,
            topic: null, relatedFqn: null, limit: 50);

        // "aged-todo" should be below the 0.1 threshold and excluded.
        Assert.DoesNotContain(hits, h => h.Memory.Content == "aged-todo");
        Assert.Contains(hits, h => h.Memory.Content == "fresh");
        Assert.Contains(hits, h => h.Memory.Content == "aged-pref");
    }

    [Fact]
    public async Task RecallAsync_OrdersByDecayDescending()
    {
        var now = DateTimeOffset.UtcNow;
        await SeedAgedAsync("low-importance-fresh", MemoryTopic.Note, 0.3, now);
        await SeedAgedAsync("high-importance-aged", MemoryTopic.Preference, 0.9, now.AddDays(-30));

        var hits = await _store.RecallAsync(
            queryEmbedding: null, scope: null, scopeId: null,
            topic: null, relatedFqn: null, limit: 50);

        Assert.True(hits.Count >= 2);
        // high-importance-aged (0.9 × large-decay) should outrank low-importance-fresh (0.3 × 1.0).
        Assert.Equal("high-importance-aged", hits[0].Memory.Content);
    }

    [Fact]
    public async Task ListAsync_IncludesForgottenMemoriesForAudit()
    {
        var now = DateTimeOffset.UtcNow;
        await SeedAgedAsync("fresh", MemoryTopic.Note, 0.5, now);
        await SeedAgedAsync("aged-todo", MemoryTopic.Todo, 0.5, now.AddDays(-60));

        var all = await _store.ListAsync(null, null, null, limit: 100);
        Assert.Equal(2, all.Count);
        Assert.Contains(all, m => m.Content == "aged-todo");
    }

    [Fact]
    public async Task ReapAsync_DeletesForgottenRows()
    {
        var now = DateTimeOffset.UtcNow;
        await SeedAgedAsync("fresh", MemoryTopic.Note, 0.5, now);
        await SeedAgedAsync("aged-todo", MemoryTopic.Todo, 0.5, now.AddDays(-60));
        await SeedAgedAsync("aged-note-low", MemoryTopic.Note, 0.2, now.AddDays(-120));

        var removed = await _store.ReapAsync();
        Assert.True(removed >= 2, $"Expected ≥2 reaped, got {removed}");

        var remaining = await _store.CountAsync();
        Assert.Equal(1L, remaining);
    }

    [Fact]
    public async Task ReapAsync_Idempotent()
    {
        var now = DateTimeOffset.UtcNow;
        await SeedAgedAsync("aged-todo", MemoryTopic.Todo, 0.5, now.AddDays(-60));

        var first = await _store.ReapAsync();
        Assert.True(first >= 1);

        var second = await _store.ReapAsync();
        Assert.Equal(0, second);
    }

    [Fact]
    public async Task ReapAsync_DoesNotTouchFreshRows()
    {
        await SeedAgedAsync("a", MemoryTopic.Note, 0.5, DateTimeOffset.UtcNow);
        await SeedAgedAsync("b", MemoryTopic.Preference, 0.9, DateTimeOffset.UtcNow);

        var removed = await _store.ReapAsync();
        Assert.Equal(0, removed);
        Assert.Equal(2L, await _store.CountAsync());
    }
}
