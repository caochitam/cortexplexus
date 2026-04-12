using CortexPlexus.App.Mcp.Tools;
using CortexPlexus.Core.Abstractions;
using CortexPlexus.Core.Models;
using NSubstitute;

namespace CortexPlexus.Mcp.Tests;

/// <summary>
/// Tests for <see cref="RepoResolver"/> — resolves a user-supplied repository name
/// (or partial match) to a <see cref="Guid"/> repo id.
///
/// R25 R24-2: critical fix for stale-repo silent hijack. When the same project
/// has been indexed twice (e.g. once via /workspace and once via _agent/&lt;name&gt;),
/// the previous <c>FirstOrDefault</c>-based resolver picked one of the duplicates
/// non-deterministically. With a stale repo, that could silently route the user's
/// query to a 38-symbol leftover instead of the fresh 2,672-symbol index.
/// R25 fix: tie-break by most recent <c>LastIndexed</c>.
/// </summary>
public sealed class RepoResolverTests
{
    private static IRepositoryStore BuildStore(params RepositoryInfo[] repos)
    {
        var store = Substitute.For<IRepositoryStore>();
        store.ListAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<RepositoryInfo>>(repos));
        return store;
    }

    /// <summary>
    /// Build a RepositoryInfo. Pass <c>lastIndexed: null</c> to leave the column NULL
    /// (simulating a row that was never successfully indexed). Default is UtcNow only
    /// when no parameter is passed at all.
    /// </summary>
    private static RepositoryInfo MakeRepo(string name, string path, DateTimeOffset? lastIndexed)
        => new(Guid.NewGuid(), name, path, DateTimeOffset.UtcNow.AddDays(-30), lastIndexed);

    private static RepositoryInfo MakeRepo(string name, string path)
        => MakeRepo(name, path, DateTimeOffset.UtcNow);

    [Fact]
    public async Task ResolveAsync_NullRepository_ReturnsNull()
    {
        var store = BuildStore(MakeRepo("CortexFlow", "/path"));
        var result = await RepoResolver.ResolveAsync(null, store);
        Assert.Null(result);
    }

    [Fact]
    public async Task ResolveAsync_WhitespaceRepository_ReturnsNull()
    {
        var store = BuildStore(MakeRepo("CortexFlow", "/path"));
        var result = await RepoResolver.ResolveAsync("   ", store);
        Assert.Null(result);
    }

    [Fact]
    public async Task ResolveAsync_ExactNameMatch_ReturnsId()
    {
        var fresh = MakeRepo("CortexFlow", "/agent/CortexFlow");
        var store = BuildStore(fresh, MakeRepo("Other", "/other"));
        var result = await RepoResolver.ResolveAsync("CortexFlow", store);
        Assert.Equal(fresh.Id, result);
    }

    [Fact]
    public async Task ResolveAsync_ExactNameMatchIsCaseInsensitive()
    {
        var repo = MakeRepo("CortexFlow", "/path");
        var store = BuildStore(repo);
        var result = await RepoResolver.ResolveAsync("cortexflow", store);
        Assert.Equal(repo.Id, result);
    }

    // === R24-2: critical fix — multiple rows with same name ===

    [Fact]
    public async Task ResolveAsync_DuplicateNames_PrefersMostRecentLastIndexed()
    {
        // Same project indexed twice. The stale row was created earlier and never
        // updated; the fresh row was just re-indexed. R25 must pick the fresh row.
        var stale = MakeRepo("CortexPlexus", "/workspace",
            lastIndexed: DateTimeOffset.Parse("2026-04-08T12:00:00Z"));
        var fresh = MakeRepo("CortexPlexus", "_agent/CortexPlexus",
            lastIndexed: DateTimeOffset.Parse("2026-04-11T22:49:00Z"));
        // Order in the list shouldn't matter — put stale FIRST so a naive
        // FirstOrDefault would pick the wrong one.
        var store = BuildStore(stale, fresh);

        var result = await RepoResolver.ResolveAsync("CortexPlexus", store);

        Assert.Equal(fresh.Id, result);
    }

    [Fact]
    public async Task ResolveAsync_DuplicateNames_FreshFirst_StillPicksFresh()
    {
        // Reverse order — should still pick the fresh one.
        var fresh = MakeRepo("CortexPlexus", "_agent/CortexPlexus",
            lastIndexed: DateTimeOffset.Parse("2026-04-11T22:49:00Z"));
        var stale = MakeRepo("CortexPlexus", "/workspace",
            lastIndexed: DateTimeOffset.Parse("2026-04-08T12:00:00Z"));
        var store = BuildStore(fresh, stale);

        var result = await RepoResolver.ResolveAsync("CortexPlexus", store);

        Assert.Equal(fresh.Id, result);
    }

    [Fact]
    public async Task ResolveAsync_DuplicateNames_OneHasNullLastIndexed_PrefersTimestamped()
    {
        // A row with null LastIndexed (never successfully indexed) should NOT win
        // over a real timestamped row.
        var newRepo = MakeRepo("CortexPlexus", "/never-indexed", lastIndexed: null);
        var fresh = MakeRepo("CortexPlexus", "_agent/CortexPlexus",
            lastIndexed: DateTimeOffset.Parse("2026-04-11T22:49:00Z"));
        var store = BuildStore(newRepo, fresh);

        var result = await RepoResolver.ResolveAsync("CortexPlexus", store);

        Assert.Equal(fresh.Id, result);
    }

    [Fact]
    public async Task ResolveAsync_PartialMatch_AlsoPrefersMostRecent()
    {
        // The same tie-break logic applies to partial matches (when no exact name
        // hit). Two repos contain "Cortex" in the name; pick the most recent.
        var stale = MakeRepo("CortexFlow-OLD", "/old",
            lastIndexed: DateTimeOffset.Parse("2026-04-08T12:00:00Z"));
        var fresh = MakeRepo("CortexFlow", "/agent",
            lastIndexed: DateTimeOffset.Parse("2026-04-11T12:00:00Z"));
        var store = BuildStore(stale, fresh);

        // "Cortex" is not an exact match for either name → falls into partial match
        var result = await RepoResolver.ResolveAsync("Cortex", store);

        Assert.Equal(fresh.Id, result);
    }

    [Fact]
    public async Task ResolveAsync_PathMatchAlsoConsidered()
    {
        // Partial match also looks at Path field. Useful for users who paste
        // a directory hint.
        var repo = MakeRepo("MyApp", "/srv/myproject/checkout");
        var store = BuildStore(repo);

        var result = await RepoResolver.ResolveAsync("myproject", store);

        Assert.Equal(repo.Id, result);
    }

    [Fact]
    public async Task ResolveAsync_NoMatch_ReturnsNull()
    {
        var store = BuildStore(MakeRepo("Foo", "/foo"), MakeRepo("Bar", "/bar"));
        var result = await RepoResolver.ResolveAsync("DoesNotExist", store);
        Assert.Null(result);
    }
}
