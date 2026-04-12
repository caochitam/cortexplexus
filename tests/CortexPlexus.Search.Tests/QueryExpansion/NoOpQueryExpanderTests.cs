using CortexPlexus.Search.QueryExpansion;

namespace CortexPlexus.Search.Tests.QueryExpansion;

public sealed class NoOpQueryExpanderTests
{
    private readonly NoOpQueryExpander _expander = new();

    [Fact]
    public void IsEnabled_ReturnsFalse()
    {
        Assert.False(_expander.IsEnabled);
    }

    [Fact]
    public async Task ExpandHydeAsync_ReturnsNull()
    {
        var result = await _expander.ExpandHydeAsync("test query");
        Assert.Null(result);
    }

    [Fact]
    public async Task ExpandMultiQueryAsync_ReturnsOriginalQuery()
    {
        var results = await _expander.ExpandMultiQueryAsync("test query");
        Assert.Single(results);
        Assert.Equal("test query", results[0]);
    }

    [Fact]
    public async Task ExpandMultiQueryAsync_IgnoresVariantCount()
    {
        var results = await _expander.ExpandMultiQueryAsync("test query", variants: 5);
        Assert.Single(results);
    }
}
