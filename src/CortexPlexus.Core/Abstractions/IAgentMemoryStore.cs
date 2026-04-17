using CortexPlexus.Core.Models;

namespace CortexPlexus.Core.Abstractions;

/// <summary>
/// Storage abstraction for agent memories. See docs/MEMORY-SYSTEM.md for the
/// feature spec; ADR-010 for why this reuses the existing PostgreSQL DB.
/// </summary>
public interface IAgentMemoryStore
{
    /// <summary>Applies the memory-table migration; idempotent. Called from startup.</summary>
    Task InitializeSchemaAsync(CancellationToken ct = default);

    /// <summary>
    /// Insert a new memory. <paramref name="embedding"/> may be null if the embedding service is not
    /// configured; recall-by-query will then miss this row. Content is the caller's responsibility
    /// to sanitize — the MCP tool layer runs <c>ISecretsScanner</c> before calling this method.
    /// </summary>
    Task<AgentMemory> SaveAsync(
        string content,
        string scope,
        string? scopeId,
        string? topic,
        double importance,
        IReadOnlyList<string>? relatedFqns,
        float[]? embedding,
        CancellationToken ct = default);

    /// <summary>
    /// Semantic + filter recall. Wave 1 implements the filter path only. Wave 2 adds embedding-based
    /// ranking + decay scoring. For Wave 1 <paramref name="queryEmbedding"/> may be ignored.
    /// </summary>
    Task<IReadOnlyList<AgentMemoryResult>> RecallAsync(
        float[]? queryEmbedding,
        string? scope,
        string? scopeId,
        string? topic,
        string? relatedFqn,
        int limit,
        CancellationToken ct = default);

    /// <summary>Pure filter/paginate. No embedding compute. For management/audit.</summary>
    Task<IReadOnlyList<AgentMemory>> ListAsync(
        string? scope,
        string? scopeId,
        string? topic,
        int limit,
        CancellationToken ct = default);

    /// <summary>Delete by id. Returns true if a row was removed, false if not found.</summary>
    Task<bool> ForgetAsync(Guid id, CancellationToken ct = default);

    /// <summary>Total row count in the store (used by Health reporting).</summary>
    Task<long> CountAsync(CancellationToken ct = default);
}
