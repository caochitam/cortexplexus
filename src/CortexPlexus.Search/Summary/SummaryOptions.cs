namespace CortexPlexus.Search.Summary;

public sealed class SummaryOptions
{
    /// <summary>Enable AI summary generation (default: false — opt-in).</summary>
    public bool Enabled { get; set; }

    /// <summary>LLM provider: "ollama" (default), "gemini", "openai", "anthropic".</summary>
    public string Provider { get; set; } = "ollama";

    /// <summary>Ollama base URL (shared with embedding).</summary>
    public string OllamaBaseUrl { get; set; } = "http://localhost:11434";

    /// <summary>Ollama model for generation (default: phi3:mini — fast, small).</summary>
    public string OllamaModel { get; set; } = "phi3:mini";

    /// <summary>API key for cloud providers (Gemini, OpenAI, Anthropic).</summary>
    public string? ApiKey { get; set; }

    /// <summary>API base URL override for cloud providers.</summary>
    public string? ApiBaseUrl { get; set; }

    /// <summary>Model name for cloud providers (e.g., "gemini-2.0-flash", "gpt-4o-mini", "claude-haiku-4-5-20251001").</summary>
    public string? Model { get; set; }

    /// <summary>Max concurrent summary requests.</summary>
    public int MaxConcurrency { get; set; } = 5;

    /// <summary>Timeout per request in seconds.</summary>
    public int TimeoutSeconds { get; set; } = 30;
}
