using CortexPlexus.App.Mcp.Tools;

namespace CortexPlexus.Mcp.Tests;

/// <summary>
/// Unit tests for the staleness helper used by list_repositories and search tools.
/// Pure functions — no fixture / DB needed.
/// </summary>
public sealed class StalenessLabelTests
{
    private static readonly DateTimeOffset Now = new(2026, 04, 18, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Format_FreshIndex_ReturnsNull()
    {
        var lastIndexed = Now.AddHours(-1); // 1 hour — within FreshWindow
        Assert.Null(StalenessLabel.Format(lastIndexed, Now));
    }

    [Fact]
    public void Format_HoursOld_ReturnsPlainLabel()
    {
        var lastIndexed = Now.AddHours(-12);
        var result = StalenessLabel.Format(lastIndexed, Now);
        Assert.NotNull(result);
        Assert.Contains("12 hours ago", result);
        Assert.DoesNotContain("STALE", result!);
    }

    [Fact]
    public void Format_TwoDaysOld_ReturnsStale()
    {
        var lastIndexed = Now.AddDays(-2);
        var result = StalenessLabel.Format(lastIndexed, Now);
        Assert.NotNull(result);
        Assert.Contains("2 days ago", result);
        Assert.Contains("STALE", result!);
        Assert.DoesNotContain("VERY", result!);
    }

    [Fact]
    public void Format_TenDaysOld_ReturnsVeryStale()
    {
        var lastIndexed = Now.AddDays(-10);
        var result = StalenessLabel.Format(lastIndexed, Now);
        Assert.NotNull(result);
        Assert.Contains("10 days ago", result);
        Assert.Contains("VERY STALE", result!);
    }

    [Fact]
    public void Format_NullLastIndexed_ReturnsNever()
    {
        var result = StalenessLabel.Format(null, Now);
        Assert.Equal("(never)", result);
    }

    [Fact]
    public void Format_SingleDayUsesSingular()
    {
        var result = StalenessLabel.Format(Now.AddDays(-1), Now);
        Assert.NotNull(result);
        Assert.Contains("1 day ago", result);
        Assert.DoesNotContain("1 days", result!);
    }

    [Fact]
    public void SearchFooter_Fresh_ReturnsNull()
    {
        Assert.Null(StalenessLabel.SearchFooter(Now.AddHours(-1), Now));
        Assert.Null(StalenessLabel.SearchFooter(Now.AddHours(-12), Now));
    }

    [Fact]
    public void SearchFooter_Stale_ReturnsWarning()
    {
        var result = StalenessLabel.SearchFooter(Now.AddDays(-2), Now);
        Assert.NotNull(result);
        Assert.Contains("STALE", result);
        Assert.Contains("ActivateAgent", result);
        Assert.Contains("2 days ago", result!);
    }

    [Fact]
    public void SearchFooter_VeryStale_Escalates()
    {
        var result = StalenessLabel.SearchFooter(Now.AddDays(-14), Now);
        Assert.NotNull(result);
        Assert.Contains("VERY STALE", result);
        Assert.Contains("14 days ago", result!);
    }

    [Fact]
    public void SearchFooter_NullLastIndexed_ReturnsNull()
    {
        // Not indexed yet — the caller already shows "never" elsewhere; footer stays quiet.
        Assert.Null(StalenessLabel.SearchFooter(null, Now));
    }
}
