namespace CortexPlexus.Embedding;

public sealed record EmbeddingOptions
{
    public string Provider { get; set; } = "gemini";
    public string? ApiKey { get; set; }
    public int Dimensions { get; set; } = 768;
    public string GeminiModel { get; set; } = "gemini-embedding-001";
    public string OllamaBaseUrl { get; set; } = "http://localhost:11434";
    public string OllamaModel { get; set; } = "nomic-embed-text";
    public string TaskType { get; set; } = "CODE_RETRIEVAL_QUERY";
    public int MaxBatchSize { get; set; } = 100;

    /// <summary>
    /// How many embedding batches to issue in parallel during indexing.
    /// <para>
    /// <c>null</c> (default) = auto-detect by provider: <c>1</c> for Ollama
    /// (CPU-bound single-thread inference makes parallelism counter-productive
    /// — confirmed in R17 ground truth on the LXC server), <c>4</c> for Gemini
    /// (request-count rate limited, parallelism is free throughput).
    /// </para>
    /// <para>
    /// Set explicitly to override auto-detection. Use <c>1</c> to force
    /// sequential behavior on any provider.
    /// </para>
    /// </summary>
    public int? MaxParallelBatches { get; set; }
}
