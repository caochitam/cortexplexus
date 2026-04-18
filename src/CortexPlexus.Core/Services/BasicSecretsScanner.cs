using System.Text.RegularExpressions;
using CortexPlexus.Core.Abstractions;

namespace CortexPlexus.Core.Services;

public sealed partial class BasicSecretsScanner : ISecretsScanner
{
    private static readonly string[] SensitiveKeywords =
    [
        "password", "passwd", "secret", "api_key", "apikey", "api-key",
        "access_token", "auth_token", "bearer", "private_key", "privatekey",
        "connection_string", "connectionstring", "conn_str"
    ];

    public string Sanitize(string content)
    {
        var result = ConnectionStringRegex().Replace(content, "[REDACTED_CONNECTION_STRING]");
        result = BearerTokenRegex().Replace(result, "[REDACTED_TOKEN]");
        result = ApiKeyRegex().Replace(result, "[REDACTED_API_KEY]");
        result = Base64KeyRegex().Replace(result, match =>
        {
            var prefix = match.Groups[1].Value;
            return $"{prefix}[REDACTED]\"";
        });
        return result;
    }

    public bool ContainsSecrets(string content)
    {
        var lower = content.ToLowerInvariant();
        return SensitiveKeywords.Any(kw => lower.Contains(kw))
            || ConnectionStringRegex().IsMatch(content)
            || BearerTokenRegex().IsMatch(content)
            || ApiKeyRegex().IsMatch(content);
    }

    [GeneratedRegex(@"(Server|Host|Data Source)=[^;""]+;[^""]*Password=[^;""]+", RegexOptions.IgnoreCase)]
    private static partial Regex ConnectionStringRegex();

    [GeneratedRegex(@"Bearer\s+[A-Za-z0-9\-._~+/]+=*", RegexOptions.IgnoreCase)]
    private static partial Regex BearerTokenRegex();

    [GeneratedRegex(@"(""[^""]*(?:key|secret|token|password|apikey)[^""]*""\s*[:=]\s*)""[^""]{8,}""", RegexOptions.IgnoreCase)]
    private static partial Regex Base64KeyRegex();

    // Well-known API-key formats with deterministic prefixes. Chosen for low
    // false-positive rate — only matches strings that could not plausibly be
    // anything other than a real credential. Added in v0.8.2 after a smoke
    // test found "AIzaSyDmPk..." (Gemini key) wasn't detected by the old scanner.
    //
    // Covered:
    //   - Google API keys (Gemini, Maps, Firebase): AIzaSy[A-Za-z0-9\-_]{33}
    //   - OpenAI: sk-proj-* / sk-* with 20+ chars
    //   - Anthropic: sk-ant-* with 20+ chars
    //   - GitHub PAT / fine-grained / OAuth / user-to-server / refresh: gh[pousr]_[A-Za-z0-9]{36,}
    //   - AWS Access Key ID: AKIA[0-9A-Z]{16}
    //   - JWT (three base64-url segments): eyJ...eyJ...
    [GeneratedRegex(
        @"AIzaSy[A-Za-z0-9\-_]{33}" +
        @"|sk-(?:proj-|ant-)?[A-Za-z0-9\-_]{20,}" +
        @"|gh[pousr]_[A-Za-z0-9]{36,}" +
        @"|AKIA[0-9A-Z]{16}" +
        @"|eyJ[A-Za-z0-9_\-]{10,}\.eyJ[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]{10,}")]
    private static partial Regex ApiKeyRegex();
}
