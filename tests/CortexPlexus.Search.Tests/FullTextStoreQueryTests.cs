using CortexPlexus.Graph;

namespace CortexPlexus.Search.Tests;

/// <summary>
/// Tests for FullTextStore query preprocessing: term splitting, OR query building, stop word filtering.
/// These are pure/static methods — no database connection needed.
/// </summary>
public sealed class FullTextStoreQueryTests
{
    // --- SplitQueryTerms ---

    [Fact]
    public void SplitQueryTerms_SingleWord_ReturnsSingleTerm()
    {
        var terms = FullTextStore.SplitQueryTerms("LocalIndexer");
        Assert.Single(terms);
        Assert.Equal("LocalIndexer", terms[0]);
    }

    [Fact]
    public void SplitQueryTerms_MultipleWords_ReturnsAll()
    {
        var terms = FullTextStore.SplitQueryTerms("LocalIndexer Agent watch");
        Assert.Equal(3, terms.Count);
        Assert.Contains("LocalIndexer", terms);
        Assert.Contains("Agent", terms);
        Assert.Contains("watch", terms);
    }

    [Fact]
    public void SplitQueryTerms_FiltersStopWords()
    {
        var terms = FullTextStore.SplitQueryTerms("how does the LocalIndexer work in this project");
        // "how", "does", "the", "in", "this" are stop words
        Assert.Contains("LocalIndexer", terms);
        Assert.Contains("work", terms);
        Assert.Contains("project", terms);
        Assert.DoesNotContain("how", terms);
        Assert.DoesNotContain("does", terms);
        Assert.DoesNotContain("the", terms);
        Assert.DoesNotContain("in", terms);
        Assert.DoesNotContain("this", terms);
    }

    [Fact]
    public void SplitQueryTerms_RemovesPunctuation()
    {
        var terms = FullTextStore.SplitQueryTerms("(LocalIndexer) [Agent] {watch}");
        Assert.Equal(3, terms.Count);
        Assert.Contains("LocalIndexer", terms);
        Assert.Contains("Agent", terms);
        Assert.Contains("watch", terms);
    }

    [Fact]
    public void SplitQueryTerms_AllStopWords_FallsBackToRawTokens()
    {
        var terms = FullTextStore.SplitQueryTerms("is the in on");
        // All stop words → fallback to raw tokens > 1 char
        Assert.Contains("is", terms);
        Assert.Contains("the", terms);
        Assert.Contains("in", terms);
        Assert.Contains("on", terms);
    }

    [Fact]
    public void SplitQueryTerms_SkipsSingleCharTokens()
    {
        var terms = FullTextStore.SplitQueryTerms("a LocalIndexer b Agent");
        // "a" and "b" are single chars (and "a" is a stop word)
        Assert.DoesNotContain("a", terms);
        Assert.DoesNotContain("b", terms);
        Assert.Contains("LocalIndexer", terms);
        Assert.Contains("Agent", terms);
    }

    [Fact]
    public void SplitQueryTerms_PreservesSingleUppercaseChar()
    {
        // Single uppercase char like "T" (generics) should be preserved
        var terms = FullTextStore.SplitQueryTerms("IEnumerable T constraint");
        Assert.Contains("T", terms);
    }

    // --- BuildOrTsQuery ---

    [Fact]
    public void BuildOrTsQuery_SingleTerm_ReturnsPrefixMatch()
    {
        var result = FullTextStore.BuildOrTsQuery(["LocalIndexer"]);
        Assert.Equal("LocalIndexer:*", result);
    }

    [Fact]
    public void BuildOrTsQuery_MultipleTerms_ReturnsOrJoined()
    {
        var result = FullTextStore.BuildOrTsQuery(["LocalIndexer", "Agent", "watch"]);
        Assert.Equal("LocalIndexer:* | Agent:* | watch:*", result);
    }

    [Fact]
    public void BuildOrTsQuery_SanitizesSpecialChars()
    {
        var result = FullTextStore.BuildOrTsQuery(["Local-Indexer", "Agent's", "watch!"]);
        // Special chars removed, only alphanumeric + underscore kept
        Assert.Contains("LocalIndexer:*", result);
        Assert.Contains("Agents:*", result);
        Assert.Contains("watch:*", result);
    }

    [Fact]
    public void BuildOrTsQuery_DeduplicatesCaseInsensitive()
    {
        var result = FullTextStore.BuildOrTsQuery(["agent", "Agent", "AGENT"]);
        // Should deduplicate — only one entry
        Assert.Equal("agent:*", result);
    }

    [Fact]
    public void BuildOrTsQuery_EmptyList_ReturnsEmpty()
    {
        var result = FullTextStore.BuildOrTsQuery([]);
        Assert.Equal("", result);
    }

    [Fact]
    public void BuildOrTsQuery_AllSpecialChars_ReturnsEmpty()
    {
        var result = FullTextStore.BuildOrTsQuery(["---", "!!!", "???"]);
        Assert.Equal("", result);
    }

    // --- End-to-end query scenario ---

    [Fact]
    public void OriginalFailingQuery_ProducesValidOrTsQuery()
    {
        // This is the exact query that failed: "LocalIndexer Agent watch index"
        var terms = FullTextStore.SplitQueryTerms("LocalIndexer Agent watch index");
        Assert.True(terms.Count >= 3, "Should extract multiple meaningful terms");
        Assert.Contains("LocalIndexer", terms);

        var orQuery = FullTextStore.BuildOrTsQuery(terms);
        Assert.Contains("LocalIndexer:*", orQuery);
        Assert.Contains("|", orQuery); // Must be OR query
    }

    [Fact]
    public void NaturalLanguageQuery_ProducesCleanTerms()
    {
        // Typical AI agent query: natural language mixed with code terms
        var terms = FullTextStore.SplitQueryTerms("how does the payment processing work in OrderService");
        Assert.Contains("payment", terms);
        Assert.Contains("processing", terms);
        Assert.Contains("work", terms);
        Assert.Contains("OrderService", terms);
        Assert.DoesNotContain("how", terms);
        Assert.DoesNotContain("does", terms);
        Assert.DoesNotContain("the", terms);
        Assert.DoesNotContain("in", terms);
    }
}
