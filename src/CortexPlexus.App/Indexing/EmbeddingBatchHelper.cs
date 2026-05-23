using System.Collections.Concurrent;
using CortexPlexus.Core.Abstractions;
using CortexPlexus.Core.Models;
using Microsoft.Extensions.Logging;

namespace CortexPlexus.App.Indexing;

/// <summary>
/// Shared helper that turns a list of <see cref="CodeSymbol"/> into FQN→embedding pairs.
/// Used by both <c>IndexingPipeline</c> (server-side parse pipeline) and
/// <c>AgentApiEndpoints./api/index/results</c> (agent uploaded symbols).
///
/// Parallelism: batches are sent through <see cref="IEmbeddingService.EmbedBatchAsync"/>
/// in parallel with bounded concurrency. On Ollama this gives near-linear speedup when
/// <c>OLLAMA_NUM_PARALLEL</c> on the server matches or exceeds <c>maxParallelBatches</c>;
/// on Gemini it simply fans out HTTPS calls within per-minute rate limits.
/// </summary>
internal static class EmbeddingBatchHelper
{
    /// <summary>Fixed batch size per HTTP request (texts packed into one EmbedBatchAsync call).</summary>
    private const int BatchSize = 50;

    public static async Task<Dictionary<string, float[]>> GenerateEmbeddingsAsync(
        IReadOnlyList<CodeSymbol> symbols,
        IEmbeddingService embeddingService,
        ISecretsScanner secretsScanner,
        ILogger logger,
        int maxParallelBatches,
        CancellationToken ct,
        IProgress<(int Done, int Total)>? batchProgress = null)
    {
        if (symbols.Count == 0)
            return new Dictionary<string, float[]>();

        // 0. Deduplicate by FQN (keep last) — mirrors VectorStore.UpsertAsync. Method
        // overloads share an FQN (the Roslyn display format omits parameters) and partial
        // classes recur across files; without this they'd be embedded repeatedly (wasted
        // Ollama calls) and, because the result is keyed by FQN, collapse into one entry —
        // which the caller's `embeddable.Count - embeddings.Count` then miscounts as
        // embedding failures (R27-2).
        var distinctSymbols = symbols
            .GroupBy(s => s.Fqn)
            .Select(g => g.Last())
            .ToList();

        // 1. Build (fqn, text) list — sanitized for secrets.
        var texts = new List<(string Fqn, string Text)>(distinctSymbols.Count);
        var skippedEmpty = 0;
        foreach (var symbol in distinctSymbols)
        {
            var text = BuildEmbeddingText(symbol);
            if (symbol.Documentation is not null)
                text += $"\n{symbol.Documentation}";
            var sanitized = secretsScanner.Sanitize(text);

            // R27-2: never send empty/whitespace text to the embedding backend — Ollama
            // returns an empty vector for blank input, which EmbedBatchAsync→result then
            // miscounts as an embedding failure. These symbols genuinely have nothing to
            // embed; skip them up front so they don't pollute the failure signal.
            if (string.IsNullOrWhiteSpace(sanitized))
            {
                skippedEmpty++;
                continue;
            }
            texts.Add((symbol.Fqn, sanitized));
        }

        if (skippedEmpty > 0)
            logger.LogInformation(
                "Embedding: skipped {Count} symbol(s) with empty text after sanitization (nothing to embed)",
                skippedEmpty);

        if (texts.Count == 0)
            return new Dictionary<string, float[]>();

        // 2. Slice into fixed-size batches.
        var batches = new List<List<(string Fqn, string Text)>>();
        for (var i = 0; i < texts.Count; i += BatchSize)
        {
            var take = Math.Min(BatchSize, texts.Count - i);
            batches.Add(texts.GetRange(i, take));
        }

        // 3. Execute batches in parallel with bounded concurrency.
        var parallelism = Math.Max(1, maxParallelBatches);
        var result = new ConcurrentDictionary<string, float[]>();

        logger.LogInformation(
            "Embedding {Symbols} symbols in {Batches} batches (parallelism={Parallel})",
            texts.Count, batches.Count, parallelism);

        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = parallelism,
            CancellationToken = ct
        };

        var completed = 0;
        await Parallel.ForEachAsync(batches, options, async (batch, token) =>
        {
            try
            {
                var embeddings = await embeddingService.EmbedBatchAsync(
                    batch.Select(t => t.Text), token);

                for (var j = 0; j < batch.Count && j < embeddings.Count; j++)
                {
                    if (embeddings[j].Length > 0)
                        result[batch[j].Fqn] = embeddings[j];
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // User cancelled the whole operation — propagate.
                throw;
            }
            catch (Exception ex)
            {
                // Catch everything else, INCLUDING TaskCanceledException from HttpClient timeout
                // (which derives from OperationCanceledException but ct is NOT cancelled).
                // R18 learning: saturated Ollama queue makes individual HTTP requests hit the
                // default 100s timeout — those must not kill the whole batch loop.
                logger.LogWarning(ex, "Failed to embed batch of {Count} symbols", batch.Count);
            }
            finally
            {
                // Report regardless of success/failure so the progress bar reflects
                // wall-clock progress, not success rate. Interlocked because Parallel
                // workers concurrent.
                var done = Interlocked.Increment(ref completed);
                batchProgress?.Report((done, batches.Count));
            }
        });

        // R27-2 diagnostic: surface symbols that were SENT but came back without a vector
        // (Ollama returned an empty embedding, or a short batch response). Sampling the
        // offenders makes the residual cause — oversize text, transient Ollama error, or a
        // batch count mismatch — visible in logs on the next real index run.
        var notEmbedded = texts.Where(t => !result.ContainsKey(t.Fqn)).ToList();
        if (notEmbedded.Count > 0)
            logger.LogWarning(
                "Embedding: {Failed} of {Sent} sent symbol(s) returned no vector. " +
                "Sample (fqn, text length): {Sample}",
                notEmbedded.Count, texts.Count,
                string.Join("; ", notEmbedded.Take(5).Select(t => $"{t.Fqn} (len={t.Text.Length})")));

        return new Dictionary<string, float[]>(result);
    }

    private static string BuildEmbeddingText(CodeSymbol symbol) => symbol switch
    {
        DocumentSection d => $"{d.Name}\n{d.Content}",
        MethodInfo m => $"{m.Signature}\n{m.ContainingTypeFqn}.{m.Name}",
        ClassInfo c => $"class {c.Name}" + (c.BaseTypeFqn is not null ? $" : {c.BaseTypeFqn}" : ""),
        InterfaceInfo i => $"interface {i.Name}",
        _ => symbol.Name
    };
}
