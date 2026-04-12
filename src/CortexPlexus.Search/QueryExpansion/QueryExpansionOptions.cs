namespace CortexPlexus.Search.QueryExpansion;

public sealed record QueryExpansionOptions
{
    /// <summary>Enable/disable query expansion globally.</summary>
    public bool Enabled { get; set; }

    /// <summary>Provider: "ollama" (default, free local) or "none".</summary>
    public string Provider { get; set; } = "ollama";

    /// <summary>Ollama base URL for text generation.</summary>
    public string OllamaBaseUrl { get; set; } = "http://localhost:11434";

    /// <summary>Ollama model for text generation (lightweight, fast).</summary>
    public string OllamaModel { get; set; } = "phi3:mini";

    /// <summary>Number of query variants for multi-query expansion.</summary>
    public int MultiQueryVariants { get; set; } = 3;

    /// <summary>Timeout in seconds for LLM generation calls.</summary>
    public int TimeoutSeconds { get; set; } = 10;
}
