using CortexPlexus.Core.Abstractions;
using CortexPlexus.Core.Models;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using Pgvector;

namespace CortexPlexus.Memory;

/// <summary>
/// PostgreSQL-backed <see cref="IAgentMemoryStore"/>. Shares the <see cref="NpgsqlDataSource"/>
/// singleton with the rest of CortexPlexus (see ADR-010). Wave 1 implements save / recall (filter
/// path only) / list / forget / count. Wave 2 adds decay-weighted recall ranking.
/// </summary>
public sealed class AgentMemoryStore(
    NpgsqlDataSource dataSource,
    ILogger<AgentMemoryStore> logger) : IAgentMemoryStore
{
    private const string MigrationResource = "CortexPlexus.Memory.Schema.Migrations.sql";

    public async Task InitializeSchemaAsync(CancellationToken ct = default)
    {
        var sql = await LoadMigrationSqlAsync(ct);
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct);
        logger.LogInformation("agent_memories schema initialized");
    }

    public async Task<AgentMemory> SaveAsync(
        string content,
        string scope,
        string? scopeId,
        string? topic,
        double importance,
        IReadOnlyList<string>? relatedFqns,
        float[]? embedding,
        CancellationToken ct = default)
    {
        if (!MemoryScope.IsValid(scope))
            throw new ArgumentException($"Invalid scope '{scope}'; must be session/project/global", nameof(scope));
        if (!MemoryTopic.IsValid(topic))
            throw new ArgumentException($"Invalid topic '{topic}'", nameof(topic));
        if (scope != MemoryScope.Global && string.IsNullOrWhiteSpace(scopeId))
            throw new ArgumentException($"scope='{scope}' requires a scope_id", nameof(scopeId));
        if (content is null || content.Length is < 1 or > 4000)
            throw new ArgumentException("Content must be 1..4000 chars", nameof(content));
        if (importance is < 0.0 or > 1.0)
            throw new ArgumentException("Importance must be in [0, 1]", nameof(importance));

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO agent_memories
                (content, scope, scope_id, topic, importance, related_fqns, embedding)
            VALUES
                (@content, @scope, @scope_id, @topic, @importance, @related_fqns, @embedding)
            RETURNING id, created_at, last_accessed_at, access_count
            """;
        cmd.Parameters.AddWithValue("content", content);
        cmd.Parameters.AddWithValue("scope", scope);
        cmd.Parameters.AddWithValue("scope_id", (object?)scopeId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("topic", (object?)topic ?? DBNull.Value);
        cmd.Parameters.AddWithValue("importance", (float)importance);
        cmd.Parameters.Add(new NpgsqlParameter("related_fqns", NpgsqlDbType.Array | NpgsqlDbType.Text)
        {
            Value = (object?)relatedFqns?.ToArray() ?? Array.Empty<string>()
        });
        cmd.Parameters.Add(embedding is null
            ? new NpgsqlParameter("embedding", DBNull.Value)
            : new NpgsqlParameter("embedding", new Vector(embedding)));

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            throw new InvalidOperationException("Insert returned no row");

        return new AgentMemory(
            Id: reader.GetGuid(0),
            Content: content,
            Scope: scope,
            ScopeId: scopeId,
            Topic: topic,
            Importance: importance,
            RelatedFqns: relatedFqns ?? Array.Empty<string>(),
            CreatedAt: reader.GetFieldValue<DateTimeOffset>(1),
            LastAccessedAt: reader.GetFieldValue<DateTimeOffset>(2),
            AccessCount: reader.GetInt32(3));
    }

    public async Task<IReadOnlyList<AgentMemoryResult>> RecallAsync(
        float[]? queryEmbedding,
        string? scope,
        string? scopeId,
        string? topic,
        string? relatedFqn,
        int limit,
        CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, 50);

        // Always filter forgotten rows out even if the reaper hasn't run yet.
        var extraWhere = $"{MemoryScoring.ScoreSqlExpression} >= {MemoryScoring.ForgetThreshold.ToString(System.Globalization.CultureInfo.InvariantCulture)}";

        // When a query embedding is supplied, combine decay with cosine similarity.
        // When not, rank by decay alone. Rows without an embedding still appear via
        // the filter query; they just contribute no semantic signal.
        string orderClause;
        if (queryEmbedding is not null)
        {
            orderClause = $"ORDER BY ({MemoryScoring.ScoreSqlExpression}) * " +
                          "COALESCE((1.0 - (embedding <=> @q)), 0.5) DESC NULLS LAST";
        }
        else
        {
            orderClause = $"ORDER BY ({MemoryScoring.ScoreSqlExpression}) DESC";
        }

        var sql = BuildFilterSql(
            scope, scopeId, topic, relatedFqn, limit,
            extraWhere: extraWhere,
            orderClause: orderClause);

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        BindFilterParameters(cmd, scope, scopeId, topic, relatedFqn);
        if (queryEmbedding is not null)
            cmd.Parameters.Add(new NpgsqlParameter("q", new Vector(queryEmbedding)));

        var results = new List<AgentMemoryResult>();
        var now = DateTimeOffset.UtcNow;
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                var memory = ReadMemory(reader);
                // Client-side score mirrors the SQL expression (no cosine term without a query).
                var score = MemoryScoring.Score(memory, now);
                results.Add(new AgentMemoryResult(memory, score));
            }
        }

        // Bump last_accessed_at + access_count for returned rows (refresh their decay).
        if (results.Count > 0)
            await RecordAccessAsync(conn, results.Select(r => r.Memory.Id).ToArray(), ct);

        return results;
    }

    public async Task<IReadOnlyList<AgentMemory>> ListAsync(
        string? scope,
        string? scopeId,
        string? topic,
        int limit,
        CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, 500);

        // list_memories shows everything the user has saved — including near-forgotten
        // rows — so callers can audit / forget explicitly. No decay filter here.
        var sql = BuildFilterSql(
            scope, scopeId, topic, relatedFqn: null, limit,
            extraWhere: null,
            orderClause: "ORDER BY importance DESC, last_accessed_at DESC");

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        BindFilterParameters(cmd, scope, scopeId, topic, relatedFqn: null);

        var results = new List<AgentMemory>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            results.Add(ReadMemory(reader));
        return results;
    }

    public async Task<bool> ForgetAsync(Guid id, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM agent_memories WHERE id = @id";
        cmd.Parameters.AddWithValue("id", id);
        var rows = await cmd.ExecuteNonQueryAsync(ct);
        return rows > 0;
    }

    public async Task<int> ClearSessionAsync(string sessionId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            throw new ArgumentException("sessionId is required", nameof(sessionId));

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        // Scoped to session rows only — never touches project/global memory.
        cmd.CommandText = "DELETE FROM agent_memories WHERE scope = 'session' AND scope_id = @id";
        cmd.Parameters.AddWithValue("id", sessionId);
        var rows = await cmd.ExecuteNonQueryAsync(ct);
        if (rows > 0)
            logger.LogInformation("Cleared {Rows} session memories for session {SessionId}", rows, sessionId);
        return rows;
    }

    public async Task<long> CountAsync(CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM agent_memories";
        var result = await cmd.ExecuteScalarAsync(ct);
        return Convert.ToInt64(result);
    }

    public async Task<int> ReapAsync(CancellationToken ct = default)
    {
        var threshold = MemoryScoring.ForgetThreshold
            .ToString(System.Globalization.CultureInfo.InvariantCulture);
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            DELETE FROM agent_memories
            WHERE {MemoryScoring.ScoreSqlExpression} < {threshold}
            """;
        var rows = await cmd.ExecuteNonQueryAsync(ct);
        if (rows > 0)
            logger.LogInformation("MemoryReaper removed {Rows} memories below forget threshold", rows);
        return rows;
    }

    private static string BuildFilterSql(
        string? scope,
        string? scopeId,
        string? topic,
        string? relatedFqn,
        int limit,
        string? extraWhere,
        string orderClause)
    {
        var where = new List<string>();
        if (!string.IsNullOrWhiteSpace(scope) && scope != "all")
            where.Add("scope = @scope");
        if (!string.IsNullOrWhiteSpace(scopeId))
            where.Add("scope_id = @scope_id");
        if (!string.IsNullOrWhiteSpace(topic))
            where.Add("topic = @topic");
        if (!string.IsNullOrWhiteSpace(relatedFqn))
            where.Add("@related_fqn = ANY(related_fqns)");
        if (!string.IsNullOrWhiteSpace(extraWhere))
            where.Add(extraWhere);

        var whereClause = where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : "";

        return $"""
            SELECT id, content, scope, scope_id, topic, importance, related_fqns,
                   created_at, last_accessed_at, access_count
            FROM agent_memories
            {whereClause}
            {orderClause}
            LIMIT {limit}
            """;
    }

    private static void BindFilterParameters(
        NpgsqlCommand cmd,
        string? scope,
        string? scopeId,
        string? topic,
        string? relatedFqn)
    {
        if (!string.IsNullOrWhiteSpace(scope) && scope != "all")
            cmd.Parameters.AddWithValue("scope", scope);
        if (!string.IsNullOrWhiteSpace(scopeId))
            cmd.Parameters.AddWithValue("scope_id", scopeId);
        if (!string.IsNullOrWhiteSpace(topic))
            cmd.Parameters.AddWithValue("topic", topic);
        if (!string.IsNullOrWhiteSpace(relatedFqn))
            cmd.Parameters.AddWithValue("related_fqn", relatedFqn);
    }

    private static AgentMemory ReadMemory(NpgsqlDataReader reader)
    {
        var fqns = reader.IsDBNull(6)
            ? Array.Empty<string>()
            : (string[])reader.GetValue(6);
        return new AgentMemory(
            Id: reader.GetGuid(0),
            Content: reader.GetString(1),
            Scope: reader.GetString(2),
            ScopeId: reader.IsDBNull(3) ? null : reader.GetString(3),
            Topic: reader.IsDBNull(4) ? null : reader.GetString(4),
            Importance: reader.GetFloat(5),
            RelatedFqns: fqns,
            CreatedAt: reader.GetFieldValue<DateTimeOffset>(7),
            LastAccessedAt: reader.GetFieldValue<DateTimeOffset>(8),
            AccessCount: reader.GetInt32(9));
    }

    private static async Task RecordAccessAsync(
        NpgsqlConnection conn,
        Guid[] ids,
        CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE agent_memories
            SET last_accessed_at = now(),
                access_count = access_count + 1
            WHERE id = ANY(@ids)
            """;
        cmd.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid)
        {
            Value = ids
        });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<string> LoadMigrationSqlAsync(CancellationToken ct)
    {
        var asm = typeof(AgentMemoryStore).Assembly;
        await using var stream = asm.GetManifestResourceStream(MigrationResource)
            ?? throw new InvalidOperationException(
                $"Embedded resource '{MigrationResource}' not found. " +
                $"Available: {string.Join(", ", asm.GetManifestResourceNames())}");
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync(ct);
    }
}
