namespace CortexPlexus.App.Mcp.Tools;

/// <summary>
/// Renders how old a repository's last-index timestamp is, with escalating
/// severity labels. Used by <c>list_repositories</c> and search-type tools
/// (v0.8.3) so agents notice stale indices before acting on stale results.
///
/// Thresholds (empirical starting point — tune after real usage):
///   &lt; 6h       : no label (index is effectively fresh)
///   6h  .. 24h : "(N hours ago)"                       plain
///   24h ..  7d : "(N days ago) ⚠️ STALE"              warn
///   &gt; 7d      : "(N days ago) 🚨 VERY STALE"         alarm
///   null       : "(never)"                             error
/// </summary>
public static class StalenessLabel
{
    private static readonly TimeSpan FreshWindow = TimeSpan.FromHours(6);
    private static readonly TimeSpan StaleThreshold = TimeSpan.FromHours(24);
    private static readonly TimeSpan VeryStaleThreshold = TimeSpan.FromDays(7);

    /// <summary>
    /// Short suffix for the "Last indexed: ..." line. Returns <c>null</c> when
    /// the index is fresh enough that the caller shouldn't append anything.
    /// </summary>
    public static string? Format(DateTimeOffset? lastIndexed, DateTimeOffset now)
    {
        if (lastIndexed is null) return "(never)";
        var age = now - lastIndexed.Value;
        if (age < FreshWindow) return null;

        var humanAge = FormatAge(age);
        if (age < StaleThreshold) return $"({humanAge} ago)";
        if (age < VeryStaleThreshold) return $"({humanAge} ago) ⚠️ STALE";
        return $"({humanAge} ago) 🚨 VERY STALE";
    }

    /// <summary>
    /// Standalone footer for search-style tools. Returns <c>null</c> when no
    /// warning is needed. Only emitted when the staleness threshold is crossed.
    /// </summary>
    public static string? SearchFooter(DateTimeOffset? lastIndexed, DateTimeOffset now)
    {
        if (lastIndexed is null) return null;
        var age = now - lastIndexed.Value;
        if (age < StaleThreshold) return null;

        var humanAge = FormatAge(age);
        var severity = age < VeryStaleThreshold ? "⚠️ STALE" : "🚨 VERY STALE";
        return
            $"--- {severity}: index last updated {humanAge} ago. Results may miss " +
            "recent code changes. Run ActivateAgent (or the agent's watch command) " +
            "to re-sync before relying on these results. ---";
    }

    private static string FormatAge(TimeSpan age)
    {
        if (age.TotalDays >= 1.0)
        {
            var days = (int)age.TotalDays;
            return days == 1 ? "1 day" : $"{days} days";
        }
        var hours = (int)age.TotalHours;
        return hours == 1 ? "1 hour" : $"{hours} hours";
    }
}
