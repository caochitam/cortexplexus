using CortexPlexus.Core.Models;

namespace CortexPlexus.Search;

public static class RrfFusion
{
    private const int K = 60;

    public static IReadOnlyList<SearchResult> Fuse(
        IReadOnlyList<(string Source, IReadOnlyList<SearchResult> Results)> rankedLists,
        int limit)
    {
        var scores = new Dictionary<string, (double Score, SearchResult Result)>();

        foreach (var (source, results) in rankedLists)
        {
            for (var rank = 0; rank < results.Count; rank++)
            {
                var result = results[rank];
                var rrfScore = 1.0 / (K + rank + 1);

                if (scores.TryGetValue(result.Fqn, out var existing))
                {
                    scores[result.Fqn] = (existing.Score + rrfScore, existing.Result);
                }
                else
                {
                    scores[result.Fqn] = (rrfScore, result with { Source = source });
                }
            }
        }

        return scores.Values
            .OrderByDescending(x => x.Score)
            .Take(limit)
            .Select(x => x.Result with { Score = x.Score })
            .ToList();
    }
}
