using CortexPlexus.Core.Models;
using CortexPlexus.Graph;
using CortexPlexus.Parsing.TreeSitter;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace CortexPlexus.Graph.Tests;

/// <summary>
/// End-to-end proof of the TypeScript cross-file resolver through the REAL query path:
/// parse a live TypeScript repo → resolver rewrites bare call names to definition FQNs →
/// AgeGraphStore.UpsertNodes/Edges into Apache AGE → QueryCallersAsync returns the callers.
///
/// This is the whole point of the resolver: before it, get_callers on a TS symbol returned
/// nothing (calls were stored as bare names disconnected from the definition vertex). The test
/// is environment-specific (needs the legacy-platform checkout) and no-ops when it is absent.
/// </summary>
[Collection("Age")]
public class LegacyPlatformCallGraphTests : IAsyncLifetime
{
    private const string RepoPath = "/home/doctorcity_com/Projects/legacy-platform";
    private const string GetTicketPriceFqn = "src/lib/rate-plan-matrix.ts:getTicketPrice";

    private readonly AgeFixture _fixture;
    private NpgsqlDataSource _dataSource = null!;
    private AgeGraphStore _store = null!;

    public LegacyPlatformCallGraphTests(AgeFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        _dataSource = _fixture.CreateDataSource();
        _store = new AgeGraphStore(_dataSource, NullLogger<AgeGraphStore>.Instance);
        await _fixture.CleanAsync(_dataSource);
    }

    public async Task DisposeAsync() => await _dataSource.DisposeAsync();

    [Fact]
    public async Task GetCallers_GetTicketPrice_ReturnsResolvedCallers_EndToEnd()
    {
        if (!Directory.Exists(RepoPath)) return; // environment-specific proof; no-op on CI

        // 1. Parse the real repo — the resolver runs inside ParseSolutionAsync.
        var parser = new TreeSitterCodeParser(NullLogger<TreeSitterCodeParser>.Instance);
        var parsed = await parser.ParseSolutionAsync(RepoPath);

        Assert.Contains(parsed.Symbols, s => s.Fqn == GetTicketPriceFqn);

        // 2. Upsert the resolved slice (definition + caller vertices + resolved Calls edges).
        //    QueryCallersAsync depth-1 only traverses direct :Calls edges, so this slice is a
        //    faithful end-to-end exercise without loading all ~82k edges of the repo.
        var repoId = await _fixture.SeedRepositoryAsync(_dataSource, "legacy-platform", RepoPath);

        var callerEdges = parsed.Relationships
            .Where(r => r.Type == RelationshipType.Calls && r.ToFqn == GetTicketPriceFqn)
            .ToList();

        var neededFqns = callerEdges.Select(r => r.FromFqn).Append(GetTicketPriceFqn).ToHashSet();
        var nodes = parsed.Symbols
            .Where(s => neededFqns.Contains(s.Fqn))
            .Select(s => WithRepoId(s, repoId))
            .ToList();

        await _store.UpsertNodesAsync(nodes);
        await _store.UpsertEdgesAsync(callerEdges);

        // 3. Query through the real MCP-backing API.
        var callers = await _store.QueryCallersAsync(GetTicketPriceFqn, depth: 1);
        var callerFqns = callers.Select(c => c.Fqn).ToHashSet();

        Assert.True(callers.Count >= 7,
            $"expected >= 7 callers of getTicketPrice via QueryCallersAsync, got {callers.Count}: [{string.Join(", ", callerFqns)}]");

        // Spot-check one caller per resolution path proven at parse time:
        Assert.Contains("src/lib/cash-tiers.ts", callerFqns);                   // relative import
        Assert.Contains("src/app/api/availability/slot/route.ts", callerFqns);  // @/ alias import
        Assert.Contains("src/app/api/bookings/route.ts", callerFqns);           // dynamic await import()
    }

    private static CodeSymbol WithRepoId(CodeSymbol s, Guid repoId) => s switch
    {
        MethodInfo m => m with { RepoId = repoId },
        ClassInfo c => c with { RepoId = repoId },
        InterfaceInfo i => i with { RepoId = repoId },
        NamespaceInfo n => n with { RepoId = repoId },
        _ => s,
    };
}
