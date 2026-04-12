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
}
