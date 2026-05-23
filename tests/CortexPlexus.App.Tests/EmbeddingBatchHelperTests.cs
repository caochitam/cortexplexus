using CortexPlexus.App.Indexing;
using CortexPlexus.Core.Abstractions;
using CortexPlexus.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace CortexPlexus.App.Tests;

/// <summary>
/// Tests for <see cref="EmbeddingBatchHelper"/> — verify parallel batch execution,
/// order-independent result aggregation, and resilience to batch failures.
///
/// Context: R14 observed 6x slowdown when indexing CortexFlow due to sequential
/// batch loop in IndexingPipeline + AgentApiEndpoints. This helper parallelizes
/// batches via Parallel.ForEachAsync with bounded concurrency.
/// </summary>
public class EmbeddingBatchHelperTests
{
    private static ISecretsScanner PassthroughScanner()
    {
        var scanner = Substitute.For<ISecretsScanner>();
        scanner.Sanitize(Arg.Any<string>()).Returns(ci => ci.Arg<string>());
        return scanner;
    }

    private static MethodInfo MakeMethod(int i) => new()
    {
        Fqn = $"NS.Class.Method{i}",
        Name = $"Method{i}",
        Kind = "method",
        Signature = $"void Method{i}()",
        ContainingTypeFqn = "NS.Class",
        ReturnType = "void",
        Accessibility = "public"
    };

    // === #1: Empty input returns empty dict (no EmbedBatchAsync call) ===
    [Fact]
    public async Task GenerateEmbeddingsAsync_EmptyInput_ReturnsEmpty()
    {
        var embedding = Substitute.For<IEmbeddingService>();
        var result = await EmbeddingBatchHelper.GenerateEmbeddingsAsync(
            [], embedding, PassthroughScanner(),
            NullLogger.Instance, maxParallelBatches: 4, CancellationToken.None);

        Assert.Empty(result);
        await embedding.DidNotReceive().EmbedBatchAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>());
    }

    // === #2: Single batch (< 50 symbols) → 1 EmbedBatchAsync call ===
    [Fact]
    public async Task GenerateEmbeddingsAsync_BelowBatchSize_SingleCall()
    {
        var symbols = Enumerable.Range(0, 10).Select(MakeMethod).Cast<CodeSymbol>().ToList();
        var embedding = Substitute.For<IEmbeddingService>();
        embedding.EmbedBatchAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult<IReadOnlyList<float[]>>(
                ci.Arg<IEnumerable<string>>().Select(_ => new[] { 0.1f, 0.2f }).ToList()));

        var result = await EmbeddingBatchHelper.GenerateEmbeddingsAsync(
            symbols, embedding, PassthroughScanner(),
            NullLogger.Instance, maxParallelBatches: 4, CancellationToken.None);

        Assert.Equal(10, result.Count);
        await embedding.Received(1).EmbedBatchAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>());
    }

    // === #3: Above batch size → chunks of 50 ===
    [Fact]
    public async Task GenerateEmbeddingsAsync_120Symbols_SplitsIntoThreeBatches()
    {
        // 120 symbols → 3 batches: [50, 50, 20]
        var symbols = Enumerable.Range(0, 120).Select(MakeMethod).Cast<CodeSymbol>().ToList();
        var embedding = Substitute.For<IEmbeddingService>();
        embedding.EmbedBatchAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult<IReadOnlyList<float[]>>(
                ci.Arg<IEnumerable<string>>().Select(_ => new[] { 1.0f }).ToList()));

        var result = await EmbeddingBatchHelper.GenerateEmbeddingsAsync(
            symbols, embedding, PassthroughScanner(),
            NullLogger.Instance, maxParallelBatches: 4, CancellationToken.None);

        Assert.Equal(120, result.Count);
        // 3 calls → 3 batches
        await embedding.Received(3).EmbedBatchAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>());
        // Every fqn is accounted for
        for (var i = 0; i < 120; i++)
            Assert.Contains($"NS.Class.Method{i}", result.Keys);
    }

    // === #4: Parallel execution — batches run concurrently, not serially ===
    [Fact]
    public async Task GenerateEmbeddingsAsync_ParallelExecution_BatchesRunConcurrently()
    {
        // 200 symbols → 4 batches. With parallelism=4 all four should be in flight together.
        var symbols = Enumerable.Range(0, 200).Select(MakeMethod).Cast<CodeSymbol>().ToList();

        var concurrent = 0;
        var peak = 0;
        var gate = new object();

        var embedding = Substitute.For<IEmbeddingService>();
        embedding.EmbedBatchAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns(async ci =>
            {
                lock (gate)
                {
                    concurrent++;
                    if (concurrent > peak) peak = concurrent;
                }
                await Task.Delay(80); // stall so peers enter in parallel
                lock (gate) { concurrent--; }
                return (IReadOnlyList<float[]>)ci.Arg<IEnumerable<string>>()
                    .Select(_ => new[] { 0.5f }).ToList();
            });

        var result = await EmbeddingBatchHelper.GenerateEmbeddingsAsync(
            symbols, embedding, PassthroughScanner(),
            NullLogger.Instance, maxParallelBatches: 4, CancellationToken.None);

        Assert.Equal(200, result.Count);
        // With 4 batches and parallelism 4, peak concurrency must exceed 1.
        // (Sequential loop would give peak == 1.)
        Assert.True(peak >= 2, $"Expected concurrent batches, peak was {peak}");
    }

    // === #5: Parallelism=1 → sequential fallback (restore legacy behavior) ===
    [Fact]
    public async Task GenerateEmbeddingsAsync_ParallelismOne_RunsSequentially()
    {
        var symbols = Enumerable.Range(0, 150).Select(MakeMethod).Cast<CodeSymbol>().ToList();

        var concurrent = 0;
        var peak = 0;
        var gate = new object();

        var embedding = Substitute.For<IEmbeddingService>();
        embedding.EmbedBatchAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns(async ci =>
            {
                lock (gate)
                {
                    concurrent++;
                    if (concurrent > peak) peak = concurrent;
                }
                await Task.Delay(30);
                lock (gate) { concurrent--; }
                return (IReadOnlyList<float[]>)ci.Arg<IEnumerable<string>>()
                    .Select(_ => new[] { 0.5f }).ToList();
            });

        await EmbeddingBatchHelper.GenerateEmbeddingsAsync(
            symbols, embedding, PassthroughScanner(),
            NullLogger.Instance, maxParallelBatches: 1, CancellationToken.None);

        Assert.Equal(1, peak);
    }

    // === #6: One batch fails → others still succeed (resilience) ===
    [Fact]
    public async Task GenerateEmbeddingsAsync_OneBatchFails_OthersSucceed()
    {
        var symbols = Enumerable.Range(0, 120).Select(MakeMethod).Cast<CodeSymbol>().ToList();

        var callIndex = 0;
        var embedding = Substitute.For<IEmbeddingService>();
        embedding.EmbedBatchAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var idx = Interlocked.Increment(ref callIndex);
                if (idx == 2) throw new HttpRequestException("boom");
                return Task.FromResult<IReadOnlyList<float[]>>(
                    ci.Arg<IEnumerable<string>>().Select(_ => new[] { 0.9f }).ToList());
            });

        var result = await EmbeddingBatchHelper.GenerateEmbeddingsAsync(
            symbols, embedding, PassthroughScanner(),
            NullLogger.Instance, maxParallelBatches: 2, CancellationToken.None);

        // 2 out of 3 batches succeed → at least 50+20 = 70 embeddings present.
        // (Exact keys depend on which batch threw, but total should be ≥ 70.)
        Assert.True(result.Count >= 70,
            $"Expected ≥70 embeddings after one batch failure, got {result.Count}");
    }

    // === R18 bug fix: TaskCanceledException from HTTP timeout must not kill the batch ===
    [Fact]
    public async Task GenerateEmbeddingsAsync_HttpTimeout_OtherBatchesSucceed()
    {
        // R18 learned: on saturated Ollama servers, HttpClient.Timeout fires a
        // TaskCanceledException (derived from OperationCanceledException) while the
        // user's CancellationToken is NOT cancelled. Previous filter
        // `when (ex is not OperationCanceledException)` let this escape and killed
        // the whole Parallel.ForEachAsync, failing the chunk. Verify the fix.
        var symbols = Enumerable.Range(0, 120).Select(MakeMethod).Cast<CodeSymbol>().ToList();

        var callIndex = 0;
        var embedding = Substitute.For<IEmbeddingService>();
        embedding.EmbedBatchAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var idx = Interlocked.Increment(ref callIndex);
                // TaskCanceledException with NO cancellation source = HttpClient timeout
                if (idx == 2) throw new TaskCanceledException("simulated HttpClient.Timeout", null);
                return Task.FromResult<IReadOnlyList<float[]>>(
                    ci.Arg<IEnumerable<string>>().Select(_ => new[] { 0.3f }).ToList());
            });

        var result = await EmbeddingBatchHelper.GenerateEmbeddingsAsync(
            symbols, embedding, PassthroughScanner(),
            NullLogger.Instance, maxParallelBatches: 2, CancellationToken.None);

        // One batch timed out (50 lost); the other 2 succeeded (50+20 = 70).
        Assert.True(result.Count >= 70,
            $"Expected ≥70 embeddings after HTTP timeout, got {result.Count}");
    }

    [Fact]
    public async Task GenerateEmbeddingsAsync_UserCancellation_Propagates()
    {
        // Conversely: if the USER cancels the outer token, we must propagate so the
        // caller can abort the indexing job. Don't swallow user intent.
        var symbols = Enumerable.Range(0, 120).Select(MakeMethod).Cast<CodeSymbol>().ToList();

        var cts = new CancellationTokenSource();
        var embedding = Substitute.For<IEmbeddingService>();
        embedding.EmbedBatchAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns(async ci =>
            {
                // On first call, cancel the user token and throw matching OCE
                cts.Cancel();
                await Task.Delay(10, CancellationToken.None);
                ci.Arg<CancellationToken>().ThrowIfCancellationRequested();
                return (IReadOnlyList<float[]>)new List<float[]>();
            });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            EmbeddingBatchHelper.GenerateEmbeddingsAsync(
                symbols, embedding, PassthroughScanner(),
                NullLogger.Instance, maxParallelBatches: 2, cts.Token));
    }

    // === #7: Empty embedding array for a text → that FQN skipped ===
    [Fact]
    public async Task GenerateEmbeddingsAsync_EmptyEmbedding_SkipsFqn()
    {
        var symbols = Enumerable.Range(0, 3).Select(MakeMethod).Cast<CodeSymbol>().ToList();
        var embedding = Substitute.For<IEmbeddingService>();
        embedding.EmbedBatchAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<float[]>>(
                new List<float[]> { new[] { 1.0f }, Array.Empty<float>(), new[] { 2.0f } }));

        var result = await EmbeddingBatchHelper.GenerateEmbeddingsAsync(
            symbols, embedding, PassthroughScanner(),
            NullLogger.Instance, maxParallelBatches: 4, CancellationToken.None);

        // Middle symbol (empty embedding) must be skipped.
        Assert.Equal(2, result.Count);
        Assert.Contains("NS.Class.Method0", result.Keys);
        Assert.DoesNotContain("NS.Class.Method1", result.Keys);
        Assert.Contains("NS.Class.Method2", result.Keys);
    }

    // === #8: Secrets scanner is applied to each text before embedding ===
    [Fact]
    public async Task GenerateEmbeddingsAsync_SanitizesTextBeforeEmbedding()
    {
        var symbols = Enumerable.Range(0, 2).Select(MakeMethod).Cast<CodeSymbol>().ToList();
        var scanner = Substitute.For<ISecretsScanner>();
        scanner.Sanitize(Arg.Any<string>()).Returns("[REDACTED]");

        List<string>? seenTexts = null;
        var embedding = Substitute.For<IEmbeddingService>();
        embedding.EmbedBatchAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                seenTexts = ci.Arg<IEnumerable<string>>().ToList();
                return Task.FromResult<IReadOnlyList<float[]>>(
                    seenTexts.Select(_ => new[] { 0.1f }).ToList());
            });

        await EmbeddingBatchHelper.GenerateEmbeddingsAsync(
            symbols, embedding, scanner,
            NullLogger.Instance, maxParallelBatches: 1, CancellationToken.None);

        Assert.NotNull(seenTexts);
        Assert.All(seenTexts, t => Assert.Equal("[REDACTED]", t));
    }

    // === R27-2: empty/whitespace text after sanitization is skipped (not sent, not failed) ===
    [Fact]
    public async Task GenerateEmbeddingsAsync_EmptyTextAfterSanitize_SkippedNotSent()
    {
        var symbols = Enumerable.Range(0, 3).Select(MakeMethod).Cast<CodeSymbol>().ToList();

        // Scanner blanks Method1's text to whitespace (simulates content fully redacted
        // or genuinely empty) — it must be skipped, never sent to the embedding backend.
        var scanner = Substitute.For<ISecretsScanner>();
        scanner.Sanitize(Arg.Any<string>())
            .Returns(ci => ci.Arg<string>().Contains("Method1") ? "   " : ci.Arg<string>());

        List<string>? seenTexts = null;
        var embedding = Substitute.For<IEmbeddingService>();
        embedding.EmbedBatchAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                seenTexts = ci.Arg<IEnumerable<string>>().ToList();
                return Task.FromResult<IReadOnlyList<float[]>>(
                    seenTexts.Select(_ => new[] { 0.1f }).ToList());
            });

        var result = await EmbeddingBatchHelper.GenerateEmbeddingsAsync(
            symbols, embedding, scanner,
            NullLogger.Instance, maxParallelBatches: 1, CancellationToken.None);

        // Method1 skipped up front: only 2 texts sent, only 2 results.
        Assert.NotNull(seenTexts);
        Assert.Equal(2, seenTexts!.Count);
        Assert.Equal(2, result.Count);
        Assert.Contains("NS.Class.Method0", result.Keys);
        Assert.DoesNotContain("NS.Class.Method1", result.Keys);
        Assert.Contains("NS.Class.Method2", result.Keys);
    }

    // === R27-2: duplicate FQNs (method overloads / partial classes) dedup, not failures ===
    [Fact]
    public async Task GenerateEmbeddingsAsync_DuplicateFqns_DedupedNotFailed()
    {
        // Two symbols share an FQN — method overloads collapse because the Roslyn display
        // format omits parameters. They must be sent once and counted once, not treated as
        // an embedding failure (root cause of the R27-2 "69 of 1124 failed" miscount).
        var dup1 = MakeMethod(0);
        var dup2 = MakeMethod(0) with { Signature = "void Method0(int x)" }; // same Fqn
        var other = MakeMethod(1);
        var symbols = new List<CodeSymbol> { dup1, dup2, other };

        List<string>? seenTexts = null;
        var embedding = Substitute.For<IEmbeddingService>();
        embedding.EmbedBatchAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                seenTexts = ci.Arg<IEnumerable<string>>().ToList();
                return Task.FromResult<IReadOnlyList<float[]>>(
                    seenTexts.Select(_ => new[] { 0.1f }).ToList());
            });

        var result = await EmbeddingBatchHelper.GenerateEmbeddingsAsync(
            symbols, embedding, PassthroughScanner(),
            NullLogger.Instance, maxParallelBatches: 1, CancellationToken.None);

        Assert.NotNull(seenTexts);
        Assert.Equal(2, seenTexts!.Count);   // 2 distinct FQNs sent, not 3
        Assert.Equal(2, result.Count);
        Assert.Contains("NS.Class.Method0", result.Keys);
        Assert.Contains("NS.Class.Method1", result.Keys);
    }

    // === R27-2: all texts empty → no backend call, empty dict ===
    [Fact]
    public async Task GenerateEmbeddingsAsync_AllTextsEmpty_NoCallEmptyResult()
    {
        var symbols = Enumerable.Range(0, 5).Select(MakeMethod).Cast<CodeSymbol>().ToList();
        var scanner = Substitute.For<ISecretsScanner>();
        scanner.Sanitize(Arg.Any<string>()).Returns("   "); // everything blanks out

        var embedding = Substitute.For<IEmbeddingService>();

        var result = await EmbeddingBatchHelper.GenerateEmbeddingsAsync(
            symbols, embedding, scanner,
            NullLogger.Instance, maxParallelBatches: 4, CancellationToken.None);

        Assert.Empty(result);
        await embedding.DidNotReceive().EmbedBatchAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>());
    }
}
