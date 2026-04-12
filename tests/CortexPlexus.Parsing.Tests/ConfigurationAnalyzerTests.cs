using CortexPlexus.Core.Models;
using CortexPlexus.Parsing.Roslyn;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CortexPlexus.Parsing.Tests;

/// <summary>
/// Tests for <see cref="ConfigurationAnalyzer"/> — the Roslyn-based C# analyzer
/// that extracts <see cref="RelationshipType.ReadsConfig"/> edges from patterns
/// like <c>IConfiguration["key"]</c>, <c>Configuration.GetSection("k")</c>,
/// <c>IOptions&lt;T&gt;</c> injection, and <c>Configuration.GetConnectionString("X")</c>.
///
/// Note: tests provide stub declarations of the relevant types from
/// Microsoft.Extensions.Configuration so the semantic model can resolve them
/// without needing the real package reference.
/// </summary>
public sealed class ConfigurationAnalyzerTests
{
    private const string ConfigStubs = """
        namespace Microsoft.Extensions.Configuration
        {
            public interface IConfiguration
            {
                string this[string key] { get; set; }
                IConfigurationSection GetSection(string key);
            }
            public interface IConfigurationRoot : IConfiguration { }
            public interface IConfigurationSection : IConfiguration { }

            public static class ConfigurationExtensions
            {
                public static string GetConnectionString(this IConfiguration config, string name)
                {
                    return null;
                }
            }
        }
        """;

    private static IReadOnlyList<Relationship> Analyze(string body)
    {
        var code = ConfigStubs + "\n" + body;
        var tree = CSharpSyntaxTree.ParseText(code);
        var compilation = CSharpCompilation.Create("Test",
            [tree],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Disable));
        var sm = compilation.GetSemanticModel(tree);
        var analyzer = new ConfigurationAnalyzer(sm);
        return analyzer.Analyze(tree).Relationships;
    }

    // === R21 Fix #8: GetConnectionString pattern ===
    [Fact]
    public void GetConnectionString_EmitsConnectionStringsPrefix()
    {
        // User smoke test reported get_config_usage("Qdrant") missing readers on CortexFlow.
        // Root cause: CortexFlow uses builder.Configuration.GetConnectionString("Qdrant")
        // which was not in the ConfigAccessMethods list. Fix: recognize the pattern
        // and emit config:ConnectionStrings:{name} so users can find it.
        var rels = Analyze("""
            namespace MyApp
            {
                public class Startup
                {
                    public void Configure(Microsoft.Extensions.Configuration.IConfiguration configuration)
                    {
                        var host = configuration.GetConnectionString("Qdrant");
                    }
                }
            }
            """);

        Assert.Contains(rels, r =>
            r.Type == RelationshipType.ReadsConfig &&
            r.ToFqn == "config:ConnectionStrings:Qdrant");
    }

    [Fact]
    public void IConfiguration_Indexer_EmitsConfigKey()
    {
        var rels = Analyze("""
            namespace MyApp
            {
                public class Startup
                {
                    public void Configure(Microsoft.Extensions.Configuration.IConfiguration config)
                    {
                        var key = config["GeminiApiKey"];
                    }
                }
            }
            """);

        Assert.Contains(rels, r =>
            r.Type == RelationshipType.ReadsConfig &&
            r.ToFqn == "config:GeminiApiKey");
    }

    [Fact]
    public void IConfiguration_GetSection_EmitsConfigKey()
    {
        var rels = Analyze("""
            namespace MyApp
            {
                public class Startup
                {
                    public void Configure(Microsoft.Extensions.Configuration.IConfiguration config)
                    {
                        var section = config.GetSection("AI");
                    }
                }
            }
            """);

        Assert.Contains(rels, r =>
            r.Type == RelationshipType.ReadsConfig &&
            r.ToFqn == "config:AI");
    }

    [Fact]
    public void NonConfigurationReceiver_DoesNotEmitEdge()
    {
        var rels = Analyze("""
            namespace MyApp
            {
                public class Cache
                {
                    private System.Collections.Generic.Dictionary<string, string> _dict = new();
                    public string Get() => _dict["some-key"];
                }
            }
            """);

        // Dictionary indexer must not be matched as IConfiguration indexer
        Assert.Empty(rels);
    }

    [Fact]
    public void GetConnectionString_OnNonConfiguration_Ignored()
    {
        // Method named GetConnectionString but called on a non-IConfiguration receiver
        // must NOT emit an edge.
        var rels = Analyze("""
            namespace MyApp
            {
                public class Fake
                {
                    public string GetConnectionString(string name) => "";
                    public void Use()
                    {
                        var x = new Fake().GetConnectionString("Ignore");
                    }
                }
            }
            """);

        Assert.Empty(rels);
    }
}
