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
        CancellationToken ct)
    {
        if (symbols.Count == 0)
            return new Dictionary<string, float[]>();

        // 1. Build (fqn, text) list — sanitized for secrets.
        var texts = new List<(string Fqn, string Text)>(symbols.Count);
        foreach (var symbol in symbols)
        {
            var text = BuildEmbeddingText(symbol);
            if (symbol.Documentation is not null)
                text += $"\n{symbol.Documentation}";
            texts.Add((symbol.Fqn, secretsScanner.Sanitize(text)));
        }

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
        });

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
