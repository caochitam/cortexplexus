using Npgsql;
using Testcontainers.PostgreSql;

namespace CortexPlexus.Memory.Tests;

/// <summary>
/// PostgreSQL container with the pgvector extension enabled. Memory tests don't need AGE,
/// so we use the lighter pgvector/pgvector:pg17 image.
/// </summary>
public sealed class MemoryFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder()
        .WithImage("pgvector/pgvector:pg17")
        .WithDatabase("cortexplexus_memory_test")
        .WithUsername("postgres")
        .WithPassword("test")
        .Build();

    public string ConnectionString => _container.GetConnectionString();

    public NpgsqlDataSource CreateDataSource()
    {
        var builder = new NpgsqlDataSourceBuilder(ConnectionString);
        builder.UseVector();
        return builder.Build();
    }

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "CREATE EXTENSION IF NOT EXISTS vector;";
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync()
    {
        await _container.DisposeAsync();
    }

    public async Task CleanAsync(NpgsqlDataSource dataSource)
    {
        await using var conn = await dataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "TRUNCATE agent_memories";
        await cmd.ExecuteNonQueryAsync();
    }
}

[CollectionDefinition("Memory")]
public class MemoryCollection : ICollectionFixture<MemoryFixture> { }
