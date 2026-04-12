using CortexPlexus.Core.Models;

namespace CortexPlexus.Search;

public sealed class ContextCompressor
{
    private const int DefaultTokenBudget = 4000;
    private const int TokensPerChar = 4; // rough estimate: 1 token ≈ 4 chars

    public string Compress(IReadOnlyList<SearchResult> results, int tokenBudget = DefaultTokenBudget)
    {
        var level = SelectLevel(results.Count, tokenBudget);
        var sb = new System.Text.StringBuilder();

        foreach (var result in results)
        {
            var entry = level switch
            {
                0 => FormatL0(result),
                1 => FormatL1(result),
                _ => FormatL2(result)
            };

            if ((sb.Length + entry.Length) / TokensPerChar > tokenBudget)
                break;

            sb.AppendLine(entry);
        }

        return sb.ToString();
    }

    private static int SelectLevel(int resultCount, int tokenBudget)
    {
        var tokensPerResult = tokenBudget / Math.Max(resultCount, 1);
        return tokensPerResult switch
        {
            >= 500 => 2,
            >= 150 => 1,
            _ => 0
        };
    }

    private static string FormatL0(SearchResult r)
        => $"[{r.Kind}] {r.Fqn}{(r.Signature is not null ? $" — {r.Signature}" : "")}";

    private static string FormatL1(SearchResult r)
    {
        var doc = r.Documentation is not null
            ? $"\n  Doc: {(r.Documentation.Length > 150 ? r.Documentation[..150] + "..." : r.Documentation)}"
            : "";
        return $"[{r.Kind}] {r.Fqn}\n  Signature: {r.Signature ?? "N/A"}\n  File: {r.FilePath}:{r.StartLine}{doc}";
    }

    private static string FormatL2(SearchResult r)
    {
        var doc = r.Documentation is not null
            ? $"\n  Doc: {(r.Documentation.Length > 200 ? r.Documentation[..200] + "..." : r.Documentation)}"
            : "";
        var summary = r.AiSummary is not null ? $"\n  Summary: {r.AiSummary}" : "";
        return $"[{r.Kind}] {r.Fqn}\n  Signature: {r.Signature ?? "N/A"}\n  File: {r.FilePath}:{r.StartLine}{doc}{summary}\n  Score: {r.Score:F4} ({r.Source})";
    }
}
