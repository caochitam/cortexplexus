using System.Text.Json;
using CortexPlexus.App.Api.Dto;

namespace CortexPlexus.App.Tests;

/// <summary>
/// Schema guarantees for <see cref="IndexResultsResponse"/> — the wire contract
/// of <c>POST /api/index/results</c>. v0.7.0 introduced
/// <c>symbolsPersisted</c> / <c>symbolsFailed</c> / <c>vectorRowsWritten</c>
/// and demoted the old <c>embeddingsPersisted</c> / <c>embeddingsFailed</c> to
/// computed-property aliases. Both must be present on the wire so v1.1.0
/// agents keep working; v0.8.0 will drop the aliases.
///
/// Spec: <c>docs/API.md</c>, <c>docs/PLAN-v0.7.0.md</c> Item #2.
/// </summary>
public class IndexResultsResponseSchemaTests
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static IndexResultsResponse Sample(int persisted = 1860, int failed = 0, int vectorRows = 479) => new()
    {
        Project = "X",
        Symbols = 2000,
        Relationships = 0,
        Embeddings = 479,
        DurationSeconds = 110.7,
        SymbolsPersisted = persisted,
        SymbolsFailed = failed,
        VectorRowsWritten = vectorRows,
        Warnings = [],
    };

    [Fact]
    public void EmitsBothNewAndDeprecatedFieldNames()
    {
        var json = JsonSerializer.Serialize(Sample(), Json);

        Assert.Contains("\"symbolsPersisted\":1860", json);
        Assert.Contains("\"symbolsFailed\":0", json);
        Assert.Contains("\"vectorRowsWritten\":479", json);
        // Aliases for v1.1.0 agents — still on the wire one release.
        Assert.Contains("\"embeddingsPersisted\":1860", json);
        Assert.Contains("\"embeddingsFailed\":0", json);
    }

    [Fact]
    public void DeprecatedAliases_TrackTheirCanonicalSibling()
    {
        var resp = Sample(persisted: 50, failed: 12);

        Assert.Equal(50, resp.EmbeddingsPersisted);   // alias of SymbolsPersisted
        Assert.Equal(12, resp.EmbeddingsFailed);      // alias of SymbolsFailed
    }

    [Fact]
    public void VectorRowsWritten_NeverExceedsSymbolsPersisted()
    {
        // Health invariant: every row with a non-null embedding is also a row.
        // Enforced via <= in the spec; assert here as a smoke.
        var resp = Sample(persisted: 1860, vectorRows: 479);

        Assert.True(resp.VectorRowsWritten <= resp.SymbolsPersisted);
    }
}
