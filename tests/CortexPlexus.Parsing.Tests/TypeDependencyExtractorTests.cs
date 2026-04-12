using CortexPlexus.Core.Models;
using CortexPlexus.Parsing.Extractors;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CortexPlexus.Parsing.Tests;

public sealed class TypeDependencyExtractorTests
{
    [Fact]
    public void Constructor_EmitsDependsOn()
    {
        var relationships = ExtractRelationships("""
            namespace MyApp;
            public interface IRepository { }
            public class OrderService
            {
                public OrderService(IRepository repo) { }
            }
            """);

        var dep = relationships.FirstOrDefault(r =>
            r.Type == RelationshipType.DependsOn &&
            r.FromFqn.Contains("OrderService") &&
            r.ToFqn.Contains("IRepository"));
        Assert.NotNull(dep);
    }

    [Fact]
    public void Constructor_MultipleDeps_EmitsAll()
    {
        var relationships = ExtractRelationships("""
            namespace MyApp;
            public interface IRepo { }
            public interface ILogger { }
            public class Svc
            {
                public Svc(IRepo repo, ILogger log) { }
            }
            """);

        var deps = relationships.Where(r =>
            r.Type == RelationshipType.DependsOn &&
            r.FromFqn.Contains("Svc")).ToList();
        Assert.Equal(2, deps.Count);
    }

    [Fact]
    public void Property_EmitsUsesType()
    {
        var relationships = ExtractRelationships("""
            namespace MyApp;
            public class Config { }
            public class Service
            {
                public Config Settings { get; set; }
            }
            """);

        var uses = relationships.FirstOrDefault(r =>
            r.Type == RelationshipType.UsesType &&
            r.FromFqn.Contains("Service") &&
            r.ToFqn.Contains("Config"));
        Assert.NotNull(uses);
    }

    [Fact]
    public void Method_ReturnType_EmitsUsesType()
    {
        var relationships = ExtractRelationships("""
            namespace MyApp;
            public class Order { }
            public class OrderService
            {
                public Order GetOrder() => null;
            }
            """);

        var uses = relationships.FirstOrDefault(r =>
            r.Type == RelationshipType.UsesType &&
            r.FromFqn.Contains("OrderService") &&
            r.ToFqn.Contains("Order"));
        Assert.NotNull(uses);
    }

    [Fact]
    public void Method_ParameterType_EmitsUsesType()
    {
        var relationships = ExtractRelationships("""
            namespace MyApp;
            public class Filter { }
            public class SearchService
            {
                public void Search(Filter filter) { }
            }
            """);

        var uses = relationships.FirstOrDefault(r =>
            r.Type == RelationshipType.UsesType &&
            r.FromFqn.Contains("SearchService") &&
            r.ToFqn.Contains("Filter"));
        Assert.NotNull(uses);
    }

    [Fact]
    public void PrimitiveTypes_NotEmitted()
    {
        var relationships = ExtractRelationships("""
            namespace MyApp;
            public class Svc
            {
                public int Count { get; set; }
                public string Name { get; set; }
                public void Run(bool flag) { }
            }
            """);

        var usesType = relationships.Where(r => r.Type == RelationshipType.UsesType).ToList();
        Assert.Empty(usesType);
    }

    [Fact]
    public void SelfReference_NotEmitted()
    {
        var relationships = ExtractRelationships("""
            namespace MyApp;
            public class Node
            {
                public Node Parent { get; set; }
            }
            """);

        var selfRef = relationships.Where(r =>
            r.Type == RelationshipType.UsesType &&
            r.FromFqn == r.ToFqn).ToList();
        Assert.Empty(selfRef);
    }

    [Fact]
    public void NoDuplicateEdges()
    {
        var relationships = ExtractRelationships("""
            namespace MyApp;
            public class Dto { }
            public class Svc
            {
                public Dto GetDto() => null;
                public void SetDto(Dto dto) { }
            }
            """);

        var usesDto = relationships.Where(r =>
            r.Type == RelationshipType.UsesType &&
            r.ToFqn.Contains("Dto")).ToList();
        // Should be 1 (deduplicated), not 2
        Assert.Single(usesDto);
    }

    // --- Helper ---

    private static IReadOnlyList<Relationship> ExtractRelationships(string code)
    {
        var tree = CSharpSyntaxTree.ParseText(code);
        var compilation = CSharpCompilation.Create("TestAssembly",
            [tree],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
             MetadataReference.CreateFromFile(typeof(Task).Assembly.Location),
             MetadataReference.CreateFromFile(typeof(System.Runtime.AssemblyTargetedPatchBandAttribute).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var semanticModel = compilation.GetSemanticModel(tree);
        var root = tree.GetRoot();

        var extractor = new TypeDependencyExtractor(semanticModel);
        extractor.Visit(root);

        return extractor.Relationships;
    }
}
