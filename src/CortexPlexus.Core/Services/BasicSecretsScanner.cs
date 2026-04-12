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
            || BearerTokenRegex().IsMatch(content);
    }

    [GeneratedRegex(@"(Server|Host|Data Source)=[^;""]+;[^""]*Password=[^;""]+", RegexOptions.IgnoreCase)]
    private static partial Regex ConnectionStringRegex();

    [GeneratedRegex(@"Bearer\s+[A-Za-z0-9\-._~+/]+=*", RegexOptions.IgnoreCase)]
    private static partial Regex BearerTokenRegex();

    [GeneratedRegex(@"(""[^""]*(?:key|secret|token|password|apikey)[^""]*""\s*[:=]\s*)""[^""]{8,}""", RegexOptions.IgnoreCase)]
    private static partial Regex Base64KeyRegex();
}
