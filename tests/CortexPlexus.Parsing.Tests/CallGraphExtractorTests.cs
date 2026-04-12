using CortexPlexus.Core.Models;
using CortexPlexus.Parsing.Extractors;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CortexPlexus.Parsing.Tests;

public sealed class CallGraphExtractorTests
{
    [Fact]
    public void ExtractsCallsRelationship()
    {
        var relationships = ExtractCallsFromCode("""
            namespace MyApp;
            public class Logger
            {
                public void Log(string msg) { }
            }
            public class OrderService
            {
                private Logger _logger = new Logger();
                public void Process()
                {
                    _logger.Log("processing");
                }
            }
            """);

        var calls = relationships.Where(r => r.Type == RelationshipType.Calls).ToList();
        Assert.Contains(calls, r => r.FromFqn.Contains("Process") && r.ToFqn.Contains("Log"));
    }

    [Fact]
    public void ExtractsCreatesRelationship()
    {
        var relationships = ExtractCallsFromCode("""
            namespace MyApp;
            public class Order { }
            public class OrderFactory
            {
                public Order Create()
                {
                    return new Order();
                }
            }
            """);

        var creates = relationships.Where(r => r.Type == RelationshipType.Creates).ToList();
        Assert.Contains(creates, r => r.FromFqn.Contains("Create") && r.ToFqn.Contains("Order"));
    }

    [Fact]
    public void DeduplicatesEdges()
    {
        var relationships = ExtractCallsFromCode("""
            namespace MyApp;
            public class Helper
            {
                public void DoWork() { }
            }
            public class Service
            {
                private Helper _h = new Helper();
                public void Run()
                {
                    _h.DoWork();
                    _h.DoWork();
                    _h.DoWork();
                }
            }
            """);

        var callsToDoWork = relationships
            .Where(r => r.Type == RelationshipType.Calls && r.ToFqn.Contains("DoWork"))
            .ToList();

        // Should deduplicate: only 1 CALLS edge from Run to DoWork
        Assert.Single(callsToDoWork);
    }

    // --- Helper ---

    private static IReadOnlyList<Relationship> ExtractCallsFromCode(string code)
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

        var extractor = new CallGraphExtractor(semanticModel);
        extractor.Visit(root);

        return extractor.Relationships;
    }
}
