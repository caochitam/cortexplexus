using CortexPlexus.Core.Models;
using CortexPlexus.Search;

namespace CortexPlexus.Search.Tests;

public sealed class RrfFusionTests
{
    [Fact]
    public void Fuse_EmptyLists_ReturnsEmpty()
    {
        var result = RrfFusion.Fuse([], limit: 10);
        Assert.Empty(result);
    }

    [Fact]
    public void Fuse_SingleSource_ReturnsRankedResults()
    {
        var results = new List<SearchResult>
        {
            MakeResult("A", 1.0),
            MakeResult("B", 0.8),
            MakeResult("C", 0.6)
        };

        var fused = RrfFusion.Fuse([("vector", results)], limit: 10);

        Assert.Equal(3, fused.Count);
        Assert.Equal("A", fused[0].Fqn);
        Assert.Equal("B", fused[1].Fqn);
        Assert.Equal("C", fused[2].Fqn);
    }

    [Fact]
    public void Fuse_TwoSources_MergesAndRanks()
    {
        var vectorResults = new List<SearchResult>
        {
            MakeResult("A", 1.0),
            MakeResult("B", 0.8)
        };

        var bm25Results = new List<SearchResult>
        {
            MakeResult("B", 1.0),
            MakeResult("C", 0.8)
        };

        var fused = RrfFusion.Fuse(
            [("vector", vectorResults), ("bm25", bm25Results)],
            limit: 10);

        // B appears in both sources → highest RRF score
        Assert.Equal("B", fused[0].Fqn);
        Assert.True(fused[0].Score > fused[1].Score);
    }

    [Fact]
    public void Fuse_RespectsLimit()
    {
        var results = Enumerable.Range(0, 20)
            .Select(i => MakeResult($"Item{i}", 1.0 - i * 0.01))
            .ToList();

        var fused = RrfFusion.Fuse([("vector", results)], limit: 5);

        Assert.Equal(5, fused.Count);
    }

    [Fact]
    public void Fuse_DuplicateAcrossSources_CombinesScores()
    {
        var source1 = new List<SearchResult> { MakeResult("X", 1.0) };
        var source2 = new List<SearchResult> { MakeResult("X", 1.0) };
        var source3 = new List<SearchResult> { MakeResult("Y", 1.0) };

        var fused = RrfFusion.Fuse(
            [("a", source1), ("b", source2), ("c", source3)],
            limit: 10);

        // X appears in 2 sources, Y in 1 → X should rank higher
        Assert.Equal("X", fused[0].Fqn);
    }

    private static SearchResult MakeResult(string fqn, double score) =>
        new(fqn, fqn, "method", $"void {fqn}()", "test.cs", 1, score, "test");
}
