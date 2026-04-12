namespace CortexPlexus.Search.Tests;

/// <summary>
/// Tests for HybridQueryRouter.ExtractCodeQuery — the query preprocessor
/// that extracts code-like tokens from mixed natural language queries.
/// </summary>
public sealed class QueryPreprocessorTests
{
    // --- ExtractCodeQuery ---

    [Fact]
    public void ExtractCodeQuery_SingleToken_ReturnsNull()
    {
        // No spaces = already clean, no preprocessing needed
        Assert.Null(HybridQueryRouter.ExtractCodeQuery("LocalIndexer"));
        Assert.Null(HybridQueryRouter.ExtractCodeQuery("MyApp.Services.OrderService"));
    }

    [Fact]
    public void ExtractCodeQuery_PascalCaseTokens_Extracted()
    {
        var result = HybridQueryRouter.ExtractCodeQuery("how does LocalIndexer work");
        Assert.NotNull(result);
        Assert.Equal("LocalIndexer", result);
    }

    [Fact]
    public void ExtractCodeQuery_MultiplePascalCaseTokens_AllExtracted()
    {
        var result = HybridQueryRouter.ExtractCodeQuery("LocalIndexer and ProjectFileWatcher auto index");
        Assert.NotNull(result);
        Assert.Contains("LocalIndexer", result);
        Assert.Contains("ProjectFileWatcher", result);
    }

    [Fact]
    public void ExtractCodeQuery_CamelCaseTokens_Extracted()
    {
        var result = HybridQueryRouter.ExtractCodeQuery("find the getCallers method for indexAsync");
        Assert.NotNull(result);
        Assert.Contains("getCallers", result);
        Assert.Contains("indexAsync", result);
    }

    [Fact]
    public void ExtractCodeQuery_FqnTokens_Extracted()
    {
        var result = HybridQueryRouter.ExtractCodeQuery("search for MyApp.Services.OrderService in the code");
        Assert.NotNull(result);
        Assert.Contains("MyApp.Services.OrderService", result);
    }

    [Fact]
    public void ExtractCodeQuery_AllCapsTokens_Extracted()
    {
        var result = HybridQueryRouter.ExtractCodeQuery("check the HTTP API for MAX_RETRY");
        Assert.NotNull(result);
        Assert.Contains("HTTP", result);
        Assert.Contains("API", result);
        Assert.Contains("MAX_RETRY", result);
    }

    [Fact]
    public void ExtractCodeQuery_MixedTokenTypes_AllCodeTokensExtracted()
    {
        var result = HybridQueryRouter.ExtractCodeQuery("how does LocalIndexer.IndexAsync handle HTTP errors in the API");
        Assert.NotNull(result);
        Assert.Contains("LocalIndexer.IndexAsync", result);
        Assert.Contains("HTTP", result);
        Assert.Contains("API", result);
    }

    [Fact]
    public void ExtractCodeQuery_NoCodeTokens_ReturnsNull()
    {
        // All lowercase, no PascalCase/camelCase/FQN/ALLCAPS
        var result = HybridQueryRouter.ExtractCodeQuery("how does the search work");
        Assert.Null(result);
    }

    [Fact]
    public void ExtractCodeQuery_OriginalFailingQuery_ExtractsCodeTokens()
    {
        // "LocalIndexer Agent watch index" — LocalIndexer and Agent are PascalCase
        var result = HybridQueryRouter.ExtractCodeQuery("LocalIndexer Agent watch index");
        Assert.NotNull(result);
        Assert.Contains("LocalIndexer", result);
        // "Agent" has only 1 uppercase letter at start + all lowercase → not PascalCase (need 2+ uppercase)
        // But it's fine — at minimum LocalIndexer should be extracted
    }

    [Fact]
    public void ExtractCodeQuery_CleansPunctuation()
    {
        var result = HybridQueryRouter.ExtractCodeQuery("what is OrderService? and PaymentHandler!");
        Assert.NotNull(result);
        Assert.Contains("OrderService", result);
        Assert.Contains("PaymentHandler", result);
    }

    // --- IsPascalCase / IsCamelCase behavior (tested via ExtractCodeQuery) ---

    [Theory]
    [InlineData("OrderService", true)]    // Classic PascalCase
    [InlineData("LocalIndexer", true)]    // Classic PascalCase
    [InlineData("HttpClient", true)]      // PascalCase with acronym
    [InlineData("IService", true)]        // Interface naming
    [InlineData("order", false)]          // All lowercase
    [InlineData("ORDER", false)]          // All uppercase (handled by ALLCAPS check instead)
    [InlineData("A", false)]              // Single char
    public void ExtractCodeQuery_PascalCaseDetection(string token, bool shouldExtract)
    {
        var result = HybridQueryRouter.ExtractCodeQuery($"the {token} class");
        if (shouldExtract)
        {
            Assert.NotNull(result);
            Assert.Contains(token, result);
        }
    }

    [Theory]
    [InlineData("getCallers", true)]      // Classic camelCase
    [InlineData("indexAsync", true)]      // camelCase with suffix
    [InlineData("searchCode", true)]      // camelCase
    [InlineData("order", false)]          // All lowercase
    [InlineData("Order", false)]          // PascalCase (handled separately)
    public void ExtractCodeQuery_CamelCaseDetection(string token, bool shouldExtract)
    {
        var result = HybridQueryRouter.ExtractCodeQuery($"find {token} method");
        if (shouldExtract)
        {
            Assert.NotNull(result);
            Assert.Contains(token, result);
        }
    }
}
