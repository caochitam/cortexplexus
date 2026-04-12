using System.ComponentModel;
using CortexPlexus.Core.Abstractions;
using CortexPlexus.Core.Models;
using CortexPlexus.Search;
using ModelContextProtocol.Server;

namespace CortexPlexus.App.Mcp.Tools;

[McpServerToolType]
public sealed class SearchTools
{
    [McpServerTool, Description("Search code by exact name or keyword using full-text search")]
    public static async Task<string> SearchCode(
        [Description("Search query (class name, method name, or keyword)")] string query,
        [Description("Repository name to search in (optional — omit to search all repos). Use ListRepositories to see available names.")] string? repository = null,
        [Description("Maximum results to return")] int limit = 20,
        [Description("Enable query expansion (HyDE + multi-query) for better recall")] bool expand = false,
        HybridQueryRouter router = default!,
        ContextCompressor compressor = default!,
        IRepositoryStore repoStore = default!)
    {
        var repoId = await RepoResolver.ResolveAsync(repository, repoStore);
        var results = await router.SearchAsync(new SearchRequest(query, SearchType.Bm25, limit, RepoId: repoId, Expand: expand));
        return results.Count == 0
            ? "No results found."
            : compressor.Compress(results);
    }

    [McpServerTool, Description("Search code using natural language semantic search with optional query expansion")]
    public static async Task<string> SemanticSearch(
        [Description("Natural language query (e.g., 'payment processing logic')")] string query,
        [Description("Repository name to search in (optional — omit to search all repos)")] string? repository = null,
        [Description("Maximum results to return")] int limit = 10,
        [Description("Enable query expansion (HyDE + multi-query) for better recall")] bool expand = false,
        HybridQueryRouter router = default!,
        ContextCompressor compressor = default!,
        IRepositoryStore repoStore = default!)
    {
        var repoId = await RepoResolver.ResolveAsync(repository, repoStore);
        var results = await router.SearchAsync(new SearchRequest(query, SearchType.Hybrid, limit, RepoId: repoId, Expand: expand));
        return results.Count == 0
            ? "No results found."
            : compressor.Compress(results);
    }
}
