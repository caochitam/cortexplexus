using CortexPlexus.Core.Models;
using CortexPlexus.Search;

namespace CortexPlexus.Search.Tests;

public sealed class ContextCompressorTests
{
    private readonly ContextCompressor _compressor = new();

    [Fact]
    public void Compress_EmptyResults_ReturnsEmpty()
    {
        var result = _compressor.Compress([]);
        Assert.Equal("", result.Trim());
    }

    [Fact]
    public void Compress_SingleResult_ContainsFqn()
    {
        var results = new List<SearchResult>
        {
            new("MyApp.OrderService.Process", "Process", "method", "void Process(Order o)", "OrderService.cs", 42, 0.95, "vector")
        };

        var output = _compressor.Compress(results);
        Assert.Contains("MyApp.OrderService.Process", output);
        Assert.Contains("method", output);
    }

    [Fact]
    public void Compress_RespectsTokenBudget()
    {
        var results = Enumerable.Range(0, 100)
            .Select(i => new SearchResult($"Namespace.Class.Method{i}", $"Method{i}", "method",
                $"void Method{i}(string arg1, string arg2, string arg3)", "file.cs", i, 0.5, "bm25"))
            .ToList();

        // Very small budget — should truncate
        var output = _compressor.Compress(results, tokenBudget: 100);
        var lineCount = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
        Assert.True(lineCount < 100, $"Expected truncation but got {lineCount} lines");
    }

    [Fact]
    public void Compress_FewResults_ShowsMoreDetail()
    {
        var results = new List<SearchResult>
        {
            new("MyApp.Service", "Service", "class", null, "Service.cs", 1, 0.9, "vector")
        };

        // Large budget + few results → should show file path (L1+)
        var output = _compressor.Compress(results, tokenBudget: 4000);
        Assert.Contains("Service.cs", output);
    }
}
