using System.ComponentModel;
using System.Text;
using System.Text.Json;
using CortexPlexus.Core.Abstractions;
using CortexPlexus.Core.Models;
using CortexPlexus.Memory;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;

namespace CortexPlexus.App.Mcp.Tools;

/// <summary>
/// Agent memory MCP tools. All four gate on <see cref="MemoryOptions.Enabled"/>;
/// when the feature is disabled they return a clear "enable via config" message
/// (see ADR-013). See docs/MEMORY-SYSTEM.md for the full spec.
/// </summary>
[McpServerToolType]
public sealed class MemoryTools
{
    private const string DisabledMessage =
        "Memory is disabled. Enable by setting Memory.Enabled=true in appsettings.json " +
        "or Memory__Enabled=true as an environment variable, then restart the server. " +
        "See docs/MEMORY-SYSTEM.md.";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    [McpServerTool, Description(
        "Save a new agent memory (preference, bug, pattern, decision, todo, or note). " +
        "Content is scanned for secrets/PII before storage; scope must be session|project|global. " +
        "Use this to remember things across sessions that aren't derivable from code.")]
    public static async Task<string> SaveMemory(
        [Description("Free-text content, 1..4000 chars. No credentials or PII.")] string? content = null,
        [Description("Scope: 'session' | 'project' | 'global'")] string? scope = null,
        [Description("scope_id: session UUID for 'session', repository ID for 'project', ignored for 'global'")] string? scopeId = null,
        [Description("Topic: 'preference' | 'pattern' | 'decision' | 'bug' | 'todo' | 'note' (optional)")] string? topic = null,
        [Description("Importance 0..1; omit to use server default (0.5)")] double? importance = null,
        [Description("Optional FQNs to link this memory to (soft link, no FK)")] string[]? relatedFqns = null,
        IAgentMemoryStore store = default!,
        IEmbeddingService embeddings = default!,
        ISecretsScanner secrets = default!,
        IOptions<MemoryOptions> options = default!,
        CancellationToken ct = default)
    {
        if (!options.Value.Enabled) return DisabledMessage;

        if (string.IsNullOrWhiteSpace(content))
            return "Error: 'content' is required (1..4000 chars).";
        if (string.IsNullOrWhiteSpace(scope))
            return "Error: 'scope' is required — one of 'session', 'project', 'global'.";
        if (!MemoryScope.IsValid(scope))
            return $"Error: invalid scope '{scope}'. Valid: 'session', 'project', 'global'.";
        if (scope != MemoryScope.Global && string.IsNullOrWhiteSpace(scopeId))
            return $"Error: scope='{scope}' requires a scope_id.";
        if (!MemoryTopic.IsValid(topic))
            return $"Error: invalid topic '{topic}'. Valid: {string.Join(", ", MemoryTopic.All)}, or omit.";

        if (secrets.ContainsSecrets(content))
            return "Error: content appears to contain secrets or credentials. Sanitize before saving.";

        var importanceValue = importance ?? options.Value.DefaultImportance;
        if (importanceValue is < 0.0 or > 1.0)
            return $"Error: importance must be in [0, 1], got {importanceValue}.";

        float[]? embedding;
        try
        {
            embedding = await embeddings.EmbedAsync(content, ct);
        }
        catch (Exception ex)
        {
            // Abort the save — without an embedding the memory is not semantically
            // recall-able, which is the whole point. User should fix the provider and retry.
            return $"Error: embedding service failed ({ex.Message}). Memory NOT saved. " +
                   "Check the embedding provider (Ollama reachable? Gemini API key?) and retry.";
        }

        try
        {
            var saved = await store.SaveAsync(
                content, scope!, scopeId, topic,
                importanceValue, relatedFqns, embedding, ct);

            return JsonSerializer.Serialize(new
            {
                id = saved.Id,
                scope = saved.Scope,
                topic = saved.Topic,
                importance = saved.Importance,
                savedAt = saved.CreatedAt,
                stored = true,
            }, JsonOpts);
        }
        catch (ArgumentException ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool, Description(
        "Semantic + filter recall of agent memories. Returns the top N matches ranked by " +
        "relevance × Weibull decay score. Bumps access_count + last_accessed_at for returned rows.")]
    public static async Task<string> RecallMemory(
        [Description("Query text for semantic search, 1..500 chars")] string? query = null,
        [Description("Scope filter: 'session' | 'project' | 'global' | 'all' (default 'all')")] string? scope = null,
        [Description("Optional scope_id to narrow within the given scope")] string? scopeId = null,
        [Description("Filter by topic (preference|pattern|decision|bug|todo|note)")] string? topic = null,
        [Description("Filter to memories linked to a specific symbol FQN")] string? relatedFqn = null,
        [Description("Max results, 1..50 (default 10)")] int limit = 10,
        IAgentMemoryStore store = default!,
        IEmbeddingService embeddings = default!,
        IOptions<MemoryOptions> options = default!,
        CancellationToken ct = default)
    {
        if (!options.Value.Enabled) return DisabledMessage;

        if (string.IsNullOrWhiteSpace(query))
            return "Error: 'query' is required (1..500 chars).";
        if (query.Length > 500)
            return "Error: 'query' must be ≤500 chars.";
        if (scope is not null && scope != "all" && !MemoryScope.IsValid(scope))
            return $"Error: invalid scope '{scope}'. Valid: 'session', 'project', 'global', 'all', or omit.";
        if (!MemoryTopic.IsValid(topic))
            return $"Error: invalid topic '{topic}'.";

        float[]? queryEmbedding;
        try
        {
            queryEmbedding = await embeddings.EmbedAsync(query, ct);
        }
        catch
        {
            queryEmbedding = null; // Fall back to filter-only recall.
        }

        var hits = await store.RecallAsync(
            queryEmbedding, scope, scopeId, topic, relatedFqn,
            Math.Clamp(limit, 1, 50), ct);

        if (hits.Count == 0)
            return "No memories matched. Try broader scope='all' or remove topic/relatedFqn filters.";

        return JsonSerializer.Serialize(new
        {
            count = hits.Count,
            memories = hits.Select(h => new
            {
                id = h.Memory.Id,
                content = h.Memory.Content,
                scope = h.Memory.Scope,
                scopeId = h.Memory.ScopeId,
                topic = h.Memory.Topic,
                importance = h.Memory.Importance,
                score = Math.Round(h.Score, 4),
                relatedFqns = h.Memory.RelatedFqns,
                createdAt = h.Memory.CreatedAt,
                lastAccessedAt = h.Memory.LastAccessedAt,
                accessCount = h.Memory.AccessCount,
            })
        }, JsonOpts);
    }

    [McpServerTool, Description(
        "List agent memories by filter — no embedding, for audit and management. " +
        "Includes near-forgotten rows (below decay threshold) so you can forget them explicitly.")]
    public static async Task<string> ListMemories(
        [Description("Scope filter: 'session' | 'project' | 'global' | 'all' (default 'all')")] string? scope = null,
        [Description("Optional scope_id")] string? scopeId = null,
        [Description("Filter by topic")] string? topic = null,
        [Description("Max results, 1..500 (default 50)")] int limit = 50,
        IAgentMemoryStore store = default!,
        IOptions<MemoryOptions> options = default!,
        CancellationToken ct = default)
    {
        if (!options.Value.Enabled) return DisabledMessage;

        if (scope is not null && scope != "all" && !MemoryScope.IsValid(scope))
            return $"Error: invalid scope '{scope}'.";
        if (!MemoryTopic.IsValid(topic))
            return $"Error: invalid topic '{topic}'.";

        var memories = await store.ListAsync(
            scope, scopeId, topic, Math.Clamp(limit, 1, 500), ct);

        return JsonSerializer.Serialize(new
        {
            count = memories.Count,
            memories = memories.Select(m => new
            {
                id = m.Id,
                content = m.Content,
                scope = m.Scope,
                scopeId = m.ScopeId,
                topic = m.Topic,
                importance = m.Importance,
                relatedFqns = m.RelatedFqns,
                createdAt = m.CreatedAt,
                lastAccessedAt = m.LastAccessedAt,
                accessCount = m.AccessCount,
            })
        }, JsonOpts);
    }

    [McpServerTool, Description(
        "Delete a specific memory by id. Use when the agent stored something wrong or " +
        "when a memory has outlived its usefulness.")]
    public static async Task<string> ForgetMemory(
        [Description("UUID of the memory to delete")] string? id = null,
        IAgentMemoryStore store = default!,
        IOptions<MemoryOptions> options = default!,
        CancellationToken ct = default)
    {
        if (!options.Value.Enabled) return DisabledMessage;

        if (string.IsNullOrWhiteSpace(id))
            return "Error: 'id' is required (memory UUID).";
        if (!Guid.TryParse(id, out var guid))
            return $"Error: invalid UUID '{id}'.";

        var removed = await store.ForgetAsync(guid, ct);
        return removed
            ? JsonSerializer.Serialize(new { forgotten = true, id = guid }, JsonOpts)
            : JsonSerializer.Serialize(new { forgotten = false, id = guid, reason = "not_found" }, JsonOpts);
    }
}
