using CortexPlexus.Core.Models;
using CortexPlexus.Parsing.Extractors;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CortexPlexus.Parsing.Tests;

public sealed class ExceptionFlowExtractorTests
{
    [Fact]
    public void DetectsThrowNewException()
    {
        var rels = ExtractFromCode("""
            using System;
            namespace MyApp;
            public class OrderService
            {
                public void Validate(int id)
                {
                    if (id <= 0) throw new ArgumentException("Invalid id");
                }
            }
            """);

        Assert.Contains(rels, r =>
            r.Type == RelationshipType.Throws &&
            r.ToFqn.Contains("ArgumentException"));
    }

    [Fact]
    public void DetectsCatchClause()
    {
        var rels = ExtractFromCode("""
            using System;
            using System.IO;
            namespace MyApp;
            public class FileService
            {
                public string Read(string path)
                {
                    try
                    {
                        return System.IO.File.ReadAllText(path);
                    }
                    catch (FileNotFoundException ex)
                    {
                        return "";
                    }
                }
            }
            """);

        Assert.Contains(rels, r =>
            r.Type == RelationshipType.Catches &&
            r.ToFqn.Contains("FileNotFoundException"));
    }

    [Fact]
    public void DetectsMultipleThrowsAndCatches()
    {
        var rels = ExtractFromCode("""
            using System;
            namespace MyApp;
            public class Processor
            {
                public void Process()
                {
                    try
                    {
                        throw new InvalidOperationException("bad state");
                    }
                    catch (InvalidOperationException)
                    {
                        throw new ApplicationException("wrapped");
                    }
                }
            }
            """);

        Assert.Contains(rels, r => r.Type == RelationshipType.Throws && r.ToFqn.Contains("InvalidOperationException"));
        Assert.Contains(rels, r => r.Type == RelationshipType.Catches && r.ToFqn.Contains("InvalidOperationException"));
        Assert.Contains(rels, r => r.Type == RelationshipType.Throws && r.ToFqn.Contains("ApplicationException"));
    }

    [Fact]
    public void NoDuplicatesForSameThrow()
    {
        var rels = ExtractFromCode("""
            using System;
            namespace MyApp;
            public class Svc
            {
                public void Do()
                {
                    throw new NotImplementedException();
                }
            }
            """);

        var throws = rels.Where(r => r.Type == RelationshipType.Throws).ToList();
        Assert.Single(throws);
    }

    private static IReadOnlyList<Relationship> ExtractFromCode(string code)
    {
        var tree = CSharpSyntaxTree.ParseText(code);
        var compilation = CSharpCompilation.Create("TestAssembly",
            [tree],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
             MetadataReference.CreateFromFile(typeof(Task).Assembly.Location),
             MetadataReference.CreateFromFile(typeof(System.IO.FileNotFoundException).Assembly.Location),
             MetadataReference.CreateFromFile(typeof(System.Runtime.AssemblyTargetedPatchBandAttribute).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var semanticModel = compilation.GetSemanticModel(tree);
        var root = tree.GetRoot();

        var extractor = new ExceptionFlowExtractor(semanticModel);
        extractor.Visit(root);

        return extractor.Relationships;
    }
}
