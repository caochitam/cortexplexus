using System.Text.Json;

namespace CortexPlexus.Agent.Tests;

/// <summary>
/// Wire-compat for the LocalIndexer UploadAck record. v0.7.0 servers send the
/// new <c>symbolsPersisted</c> / <c>symbolsFailed</c> / <c>vectorRowsWritten</c>
/// names; pre-v0.7.0 servers sent only the now-deprecated
/// <c>embeddingsPersisted</c> / <c>embeddingsFailed</c> aliases. The agent
/// must parse both for one release; v0.8.0 drops the old names.
///
/// Spec: <c>docs/PLAN-v0.7.0.md</c> Item #2, <c>docs/API.md</c>.
/// </summary>
public class UploadAckCompatTests
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private static T Parse<T>(string json) => JsonSerializer.Deserialize<T>(json, Json)!;

    [Fact]
    public void NewServerSchema_PrefersSymbolsFields()
    {
        // v0.7.0+ server response — both new and deprecated keys present
        // (deprecated kept for one release as alias).
        var json = """
            {
              "project":"X","symbols":2000,"relationships":0,"embeddings":479,
              "symbolsPersisted":1860,"symbolsFailed":0,"vectorRowsWritten":479,
              "embeddingsPersisted":1860,"embeddingsFailed":0,
              "warnings":[],"durationSeconds":110.7
            }
            """;

        var ack = Parse<LocalIndexer.UploadAck>(json);

        Assert.Equal(1860, ack.Persisted);
        Assert.Equal(0, ack.Failed);
        Assert.Equal(479, ack.VectorRows);
    }

    [Fact]
    public void OldServerSchema_FallsBackToEmbeddingsFields()
    {
        // Pre-v0.7.0 server — only the deprecated names present.
        var json = """
            {
              "project":"X","symbols":2000,"relationships":0,"embeddings":479,
              "embeddingsPersisted":1860,"embeddingsFailed":0,
              "warnings":[],"durationSeconds":110.7
            }
            """;

        var ack = Parse<LocalIndexer.UploadAck>(json);

        Assert.Equal(1860, ack.Persisted);
        Assert.Equal(0, ack.Failed);
        // VectorRows is new — old servers don't send it; accessor defaults to 0.
        Assert.Equal(0, ack.VectorRows);
    }

    [Fact]
    public void OldServerSchema_DetectsPartialPersistFailure()
    {
        // Old server reporting the issue-#1-style silent drop via deprecated names.
        var json = """
            {
              "project":"X","symbols":1710,"relationships":0,"embeddings":1710,
              "embeddingsPersisted":0,"embeddingsFailed":1710,
              "warnings":["vector_upsert: 1710 of 1710 symbols failed to persist."],
              "durationSeconds":3.2
            }
            """;

        var ack = Parse<LocalIndexer.UploadAck>(json);

        Assert.Equal(1710, ack.Failed);
        Assert.Equal(0, ack.Persisted);
        Assert.NotNull(ack.Warnings);
        Assert.Single(ack.Warnings);
    }

    [Fact]
    public void NewSchemaWins_WhenBothPresentDisagree()
    {
        // Defensive: if a future server emits divergent old + new (e.g. by
        // mistake during a refactor), the new fields are authoritative.
        var json = """
            {
              "project":"X","symbols":100,"relationships":0,"embeddings":50,
              "symbolsPersisted":50,"symbolsFailed":50,"vectorRowsWritten":40,
              "embeddingsPersisted":999,"embeddingsFailed":999,
              "warnings":[],"durationSeconds":1.0
            }
            """;

        var ack = Parse<LocalIndexer.UploadAck>(json);

        Assert.Equal(50, ack.Persisted);
        Assert.Equal(50, ack.Failed);
        Assert.Equal(40, ack.VectorRows);
    }

    [Fact]
    public void EmptyResponse_DefaultsToZero()
    {
        // Pathological: server returns minimal response with no count fields.
        // Agent treats as "no persist info" — Persisted/Failed/VectorRows == 0.
        var json = """
            {"project":"X","symbols":0,"relationships":0,"embeddings":0,"durationSeconds":0}
            """;

        var ack = Parse<LocalIndexer.UploadAck>(json);

        Assert.Equal(0, ack.Persisted);
        Assert.Equal(0, ack.Failed);
        Assert.Equal(0, ack.VectorRows);
    }
}
