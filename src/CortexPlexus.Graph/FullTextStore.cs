using System.Text.RegularExpressions;
using CortexPlexus.Core.Abstractions;
using CortexPlexus.Core.Models;
using Npgsql;

namespace CortexPlexus.Graph;

/// <summary>
/// IFullTextStore implementation using PostgreSQL tsvector/tsquery full-text search.
/// Searches the generated search_text column on code_symbols.
/// Multi-strategy: AND tsquery → OR tsquery → per-term ILIKE.
/// </summary>
public sealed partial class FullTextStore(NpgsqlDataSource dataSource) : IFullTextStore
{
    // Common English stop words that add noise to code searches
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "the", "is", "are", "was", "were", "be", "been", "being",
        "have", "has", "had", "do", "does", "did", "will", "would", "could",
        "should", "may", "might", "shall", "can", "need", "must",
        "in", "on", "at", "to", "for", "of", "with", "by", "from", "as",
        "into", "through", "during", "before", "after", "above", "below",
        "and", "but", "or", "nor", "not", "so", "yet", "both", "either",
        "this", "that", "these", "those", "it", "its",
        "what", "which", "who", "whom", "how", "where", "when", "why",
        "all", "each", "every", "any", "few", "more", "most", "some",
        "about", "between", "under", "over", "such", "only", "also", "than"
    };

    public async Task<IReadOnlyList<SearchResult>> SearchAsync(
        string query,
        int limit = 10,
        Guid? repoId = null,
        string? kind = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return [];

        // Strategy 1: tsvector AND (all terms must match — precise)
        var results = await TsVectorSearchAsync(query, limit, repoId, kind, ct);
        if (results.Count > 0) return results;

        // Strategy 2: tsvector OR (any term matches — broader recall)
        var terms = SplitQueryTerms(query);
        if (terms.Count > 1)
        {
            results = await TsVectorOrSearchAsync(terms, limit, repoId, kind, ct);
            if (results.Count > 0) return results;
        }

        // Strategy 3: ILIKE per-term (fallback for CamelCase, partial matches)
        results = await ILikePerTermSearchAsync(terms, limit, repoId, kind, ct);

        return results;
    }

    /// <summary>
    /// Splits a query into meaningful search terms.
    /// Filters stop words, splits PascalCase, keeps code-like tokens.
    /// </summary>
    internal static List<string> SplitQueryTerms(string query)
    {
        // Split on whitespace and common punctuation
        var rawTokens = query.Split([' ', ',', ';', ':', '(', ')', '[', ']', '{', '}', '"', '\''],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var terms = new List<string>();
        foreach (var token in rawTokens)
        {
            // Skip stop words
            if (StopWords.Contains(token)) continue;

            // Skip very short tokens (1 char) unless uppercase (e.g., "T" for generics)
            if (token.Length == 1 && !char.IsUpper(token[0])) continue;

            terms.Add(token);
        }

        // If all tokens were stop words, use original tokens (minus very short ones)
        if (terms.Count == 0)
        {
            terms = rawTokens.Where(t => t.Length > 1).ToList();
        }

        return terms;
    }

    /// <summary>
    /// Builds a sanitized OR tsquery string from multiple terms.
    /// Each term is sanitized for safe use in to_tsquery().
    /// </summary>
    internal static string BuildOrTsQuery(IReadOnlyList<string> terms)
    {
        var sanitized = terms
            .Select(t => SanitizeTsQueryTerm().Replace(t, ""))
            .Where(t => t.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(t => $"{t}:*") // prefix match for partial terms
            .ToList();

        return sanitized.Count > 0 ? string.Join(" | ", sanitized) : "";
    }

    private async Task<IReadOnlyList<SearchResult>> TsVectorSearchAsync(
        string query, int limit, Guid? repoId, string? kind, CancellationToken ct)
    {
        var filters = new List<string>
        {
            "search_text @@ plainto_tsquery('english', @query)"
        };

        if (repoId.HasValue) filters.Add("repo_id = @repoId");
        if (!string.IsNullOrEmpty(kind)) filters.Add("kind = @kind");

        var whereClause = string.Join(" AND ", filters);
        var weights = "'{0.1, 0.2, 0.4, 1.0}'";
        var sql = $"""
            SELECT fqn, name, kind, signature, file_path, start_line,
                   ts_rank({weights}, search_text, plainto_tsquery('english', @query)) AS score,
                   documentation, summary
            FROM code_symbols
            WHERE {whereClause}
            ORDER BY score DESC
            LIMIT @limit
            """;

        return await ExecuteSearchAsync(sql, query, limit, repoId, kind, ct);
    }

    /// <summary>
    /// tsvector OR search: any term matching scores the result.
    /// Results with more matching terms rank higher via ts_rank.
    /// </summary>
    private async Task<IReadOnlyList<SearchResult>> TsVectorOrSearchAsync(
        IReadOnlyList<string> terms, int limit, Guid? repoId, string? kind, CancellationToken ct)
    {
        var orQuery = BuildOrTsQuery(terms);
        if (string.IsNullOrEmpty(orQuery)) return [];

        var filters = new List<string>
        {
            "search_text @@ to_tsquery('english', @query)"
        };

        if (repoId.HasValue) filters.Add("repo_id = @repoId");
        if (!string.IsNullOrEmpty(kind)) filters.Add("kind = @kind");

        var whereClause = string.Join(" AND ", filters);
        var weights = "'{0.1, 0.2, 0.4, 1.0}'";
        var sql = $"""
            SELECT fqn, name, kind, signature, file_path, start_line,
                   ts_rank({weights}, search_text, to_tsquery('english', @query)) AS score,
                   documentation, summary
            FROM code_symbols
            WHERE {whereClause}
            ORDER BY score DESC
            LIMIT @limit
            """;

        return await ExecuteSearchAsync(sql, orQuery, limit, repoId, kind, ct);
    }

    /// <summary>
    /// ILIKE per-term search: matches any term in name, fqn, or signature.
    /// Ranks by number of matching terms (more matches = higher score).
    /// </summary>
    private async Task<IReadOnlyList<SearchResult>> ILikePerTermSearchAsync(
        IReadOnlyList<string> terms, int limit, Guid? repoId, string? kind, CancellationToken ct)
    {
        if (terms.Count == 0) return [];

        // Build per-term ILIKE conditions with match counting for ranking
        var termConditions = new List<string>();
        var termScoreFragments = new List<string>();

        for (var i = 0; i < terms.Count; i++)
        {
            var paramName = $"@term{i}";
            termConditions.Add($"(name ILIKE {paramName} OR fqn ILIKE {paramName} OR COALESCE(signature,'') ILIKE {paramName})");
            termScoreFragments.Add($"CASE WHEN name ILIKE {paramName} OR fqn ILIKE {paramName} OR COALESCE(signature,'') ILIKE {paramName} THEN 1 ELSE 0 END");
        }

        var matchAny = string.Join(" OR ", termConditions);
        var scoreExpr = string.Join(" + ", termScoreFragments);

        var filters = new List<string> { $"({matchAny})" };
        if (repoId.HasValue) filters.Add("repo_id = @repoId");
        if (!string.IsNullOrEmpty(kind)) filters.Add("kind = @kind");

        var whereClause = string.Join(" AND ", filters);
        var sql = $"""
            SELECT fqn, name, kind, signature, file_path, start_line,
                   ({scoreExpr})::float / {terms.Count} AS score,
                   documentation, summary
            FROM code_symbols
            WHERE {whereClause}
            ORDER BY ({scoreExpr}) DESC, length(name)
            LIMIT @limit
            """;

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;

        for (var i = 0; i < terms.Count; i++)
            cmd.Parameters.AddWithValue($"@term{i}", $"%{terms[i]}%");

        cmd.Parameters.AddWithValue("@limit", limit);
        if (repoId.HasValue) cmd.Parameters.AddWithValue("@repoId", repoId.Value);
        if (!string.IsNullOrEmpty(kind)) cmd.Parameters.AddWithValue("@kind", kind);

        return await ReadResultsAsync(cmd, "FullText:ILIKE", ct);
    }

    private async Task<IReadOnlyList<SearchResult>> ExecuteSearchAsync(
        string sql, string query, int limit, Guid? repoId, string? kind, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;

        cmd.Parameters.AddWithValue("@query", query);
        cmd.Parameters.AddWithValue("@limit", limit);
        if (repoId.HasValue) cmd.Parameters.AddWithValue("@repoId", repoId.Value);
        if (!string.IsNullOrEmpty(kind)) cmd.Parameters.AddWithValue("@kind", kind);

        return await ReadResultsAsync(cmd, "FullText", ct);
    }

    private static async Task<IReadOnlyList<SearchResult>> ReadResultsAsync(
        NpgsqlCommand cmd, string source, CancellationToken ct)
    {
        var results = new List<SearchResult>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(new SearchResult(
                Fqn: reader.GetString(0),
                Name: reader.GetString(1),
                Kind: reader.GetString(2),
                Signature: reader.IsDBNull(3) ? null : reader.GetString(3),
                FilePath: reader.IsDBNull(4) ? null : reader.GetString(4),
                StartLine: reader.IsDBNull(5) ? null : reader.GetInt32(5),
                Score: reader.GetDouble(6),
                Source: source,
                Documentation: reader.IsDBNull(7) ? null : reader.GetString(7),
                AiSummary: reader.IsDBNull(8) ? null : reader.GetString(8)
            ));
        }
        return results;
    }

    [GeneratedRegex(@"[^a-zA-Z0-9_]")]
    private static partial Regex SanitizeTsQueryTerm();
}
