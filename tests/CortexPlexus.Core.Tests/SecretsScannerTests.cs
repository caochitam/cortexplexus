using CortexPlexus.Core.Services;

namespace CortexPlexus.Core.Tests;

public sealed class SecretsScannerTests
{
    private readonly BasicSecretsScanner _scanner = new();

    [Theory]
    [InlineData("Server=myserver;Database=mydb;User Id=admin;Password=s3cret123;", true)]
    [InlineData("Host=localhost;Password=test123;", true)]
    [InlineData("Data Source=srv;Password=abc;", true)]
    [InlineData("Bearer eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0", true)]
    [InlineData("public class MyService { }", false)]
    [InlineData("var x = 42;", false)]
    public void ContainsSecrets_DetectsCorrectly(string content, bool expected)
    {
        Assert.Equal(expected, _scanner.ContainsSecrets(content));
    }

    [Fact]
    public void Sanitize_RedactsConnectionString()
    {
        var input = "Server=prod.db.com;Database=app;Password=SuperSecret;";
        var result = _scanner.Sanitize(input);
        Assert.Contains("[REDACTED_CONNECTION_STRING]", result);
        Assert.DoesNotContain("SuperSecret", result);
    }

    [Fact]
    public void Sanitize_RedactsBearerToken()
    {
        var input = "Authorization: Bearer eyJhbGciOiJIUzI1NiJ9.longtoken";
        var result = _scanner.Sanitize(input);
        Assert.Contains("[REDACTED_TOKEN]", result);
        Assert.DoesNotContain("eyJhbGci", result);
    }

    [Fact]
    public void Sanitize_PreservesNormalCode()
    {
        var input = "public void ProcessPayment(decimal amount) { }";
        var result = _scanner.Sanitize(input);
        Assert.Equal(input, result);
    }

    // v0.8.2: well-known API-key formats. Smoke test on 2026-04-18 caught
    // a Gemini key "AIzaSyDmPk..." being stored verbatim because only
    // keyword-based detection ran. These tests guard the regression.

    [Theory]
    [InlineData("My Gemini key is AIzaSyDmPkX1Y2Z3A4B5C6D7E8F9G0H1I2J3K4L5M for project X", true)]
    [InlineData("openai uses sk-proj-abc123DEF456ghi789JKL0mnoPQR3stu for completion", true)]
    [InlineData("Anthropic client reads sk-ant-api03-zzZZaaBBccDDeeFFggHH for chat", true)]
    [InlineData("github pat: ghp_abcdefghijklmnopqrstuvwxyz0123456789", true)]
    [InlineData("AWS access: AKIAIOSFODNN7EXAMPLE is the old sample", true)]
    [InlineData("JWT: eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.dozjgNryP4J3jVmNHl0w5N_XgL0Hk5Q", true)]
    // False-positive guards — similar-looking strings that are NOT keys
    [InlineData("Result<T> where T : class", false)]
    [InlineData("Consider pattern AIzaSy in this random sentence", false)]  // AIzaSy alone, no 33 chars after
    [InlineData("sk-abc", false)] // too short for OpenAI
    [InlineData("the quick brown fox jumps over the lazy dog", false)]
    public void ContainsSecrets_DetectsApiKeyFormats(string content, bool expected)
    {
        Assert.Equal(expected, _scanner.ContainsSecrets(content));
    }

    [Fact]
    public void Sanitize_RedactsGoogleApiKey()
    {
        var input = "Use AIzaSyDmPkX1Y2Z3A4B5C6D7E8F9G0H1I2J3K4L5M in the config";
        var result = _scanner.Sanitize(input);
        Assert.Contains("[REDACTED_API_KEY]", result);
        Assert.DoesNotContain("AIzaSyDmPk", result);
    }

    [Fact]
    public void Sanitize_RedactsOpenAiKey()
    {
        var input = "sk-proj-abc123DEF456ghi789JKL0mnoPQR3stu is the key";
        var result = _scanner.Sanitize(input);
        Assert.Contains("[REDACTED_API_KEY]", result);
        Assert.DoesNotContain("sk-proj-abc", result);
    }

    [Fact]
    public void Sanitize_RedactsGithubPat()
    {
        var input = "token=ghp_abcdefghijklmnopqrstuvwxyz0123456789";
        var result = _scanner.Sanitize(input);
        Assert.Contains("[REDACTED_API_KEY]", result);
        Assert.DoesNotContain("ghp_abc", result);
    }
}
