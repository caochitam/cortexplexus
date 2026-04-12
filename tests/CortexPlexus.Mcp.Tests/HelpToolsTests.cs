using CortexPlexus.App.Mcp.Tools;

namespace CortexPlexus.Mcp.Tests;

/// <summary>
/// Tests for <see cref="HelpTools"/> — the GetHelp MCP tool that returns
/// usage guides for the CortexPlexus tool surface.
///
/// R22 Fix #12: warn loudly when an unknown topic is passed instead of
/// silently falling back to the default. Before R22, get_help(topic="bogus")
/// returned the quickstart with no indication that the topic filter was
/// ignored — users would think their filter applied.
/// </summary>
public sealed class HelpToolsTests
{
    [Theory]
    [InlineData("quick-start")]
    [InlineData("tools")]
    [InlineData("indexing")]
    [InlineData("strategies")]
    [InlineData("when-to-use")]
    [InlineData("all")]
    public void GetHelp_ValidTopic_ReturnsContentWithoutWarning(string topic)
    {
        var result = HelpTools.GetHelp(topic);

        Assert.NotNull(result);
        Assert.NotEmpty(result);
        // No warning should appear for valid topics
        Assert.DoesNotContain("Unknown topic", result);
    }

    [Theory]
    [InlineData("QUICK-START")]
    [InlineData("Quick-Start")]
    [InlineData("ALL")]
    public void GetHelp_TopicCaseInsensitive(string topic)
    {
        var result = HelpTools.GetHelp(topic);
        Assert.DoesNotContain("Unknown topic", result);
    }

    // === R22 Fix #12 ===
    [Fact]
    public void GetHelp_UnknownTopic_WarnsAndListsValidTopics()
    {
        // Smoke test reported get_help(topic="nonexistent-topic") silently
        // returned the quickstart. After R22 it should warn loudly.
        var result = HelpTools.GetHelp("nonexistent-topic");

        Assert.Contains("Unknown topic", result);
        Assert.Contains("'nonexistent-topic'", result);
        // The warning should also list valid topics so users can fix their call
        Assert.Contains("Valid topics:", result);
        Assert.Contains("'quick-start'", result);
        Assert.Contains("'tools'", result);
        Assert.Contains("'indexing'", result);
        Assert.Contains("'all'", result);
        // Default fallback content should still be returned alongside the warning
        Assert.Contains("CortexPlexus", result);
    }

    [Fact]
    public void GetHelp_EmptyTopic_TreatedAsUnknown()
    {
        // Empty string isn't in the valid topic list — should warn.
        var result = HelpTools.GetHelp("");

        Assert.Contains("Unknown topic", result);
    }

    [Fact]
    public void GetHelp_DefaultParam_ReturnsAllSections()
    {
        // No argument → default "all" → no warning.
        var result = HelpTools.GetHelp();

        Assert.DoesNotContain("Unknown topic", result);
        Assert.NotEmpty(result);
    }
}
