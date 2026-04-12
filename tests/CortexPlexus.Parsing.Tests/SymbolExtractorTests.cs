using CortexPlexus.Core.Models;
using CortexPlexus.Parsing.Extractors;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CortexPlexus.Parsing.Tests;

public sealed class SymbolExtractorTests
{
    [Fact]
    public void ExtractsClass()
    {
        var (symbols, _) = ExtractFromCode("""
            namespace MyApp;
            public class OrderService { }
            """);

        var cls = symbols.OfType<ClassInfo>().FirstOrDefault(s => s.Name == "OrderService");
        Assert.NotNull(cls);
        Assert.Equal("class", cls.Kind);
        Assert.Equal("public", cls.Accessibility);
        Assert.Contains("MyApp.OrderService", cls.Fqn);
    }

    [Fact]
    public void ExtractsInterface()
    {
        var (symbols, _) = ExtractFromCode("""
            namespace MyApp;
            public interface IOrderService { }
            """);

        var iface = symbols.OfType<InterfaceInfo>().FirstOrDefault(s => s.Name == "IOrderService");
        Assert.NotNull(iface);
        Assert.Equal("interface", iface.Kind);
    }

    [Fact]
    public void ExtractsMethod()
    {
        var (symbols, _) = ExtractFromCode("""
            namespace MyApp;
            public class OrderService
            {
                public async Task<bool> ProcessOrderAsync(int orderId) => true;
            }
            """);

        var method = symbols.OfType<MethodInfo>().FirstOrDefault(s => s.Name == "ProcessOrderAsync");
        Assert.NotNull(method);
        Assert.Equal("method", method.Kind);
        Assert.True(method.IsAsync);
        Assert.Contains("ProcessOrderAsync", method.Signature);
    }

    [Fact]
    public void ExtractsInheritanceRelationship()
    {
        var (_, relationships) = ExtractFromCode("""
            namespace MyApp;
            public class Animal { }
            public class Dog : Animal { }
            """);

        var inherits = relationships.FirstOrDefault(r =>
            r.Type == RelationshipType.Inherits && r.ToFqn.Contains("Animal"));
        Assert.NotNull(inherits);
        Assert.Contains("Dog", inherits.FromFqn);
    }

    [Fact]
    public void ExtractsImplementsRelationship()
    {
        var (_, relationships) = ExtractFromCode("""
            namespace MyApp;
            public interface IService { }
            public class MyService : IService { }
            """);

        var implements = relationships.FirstOrDefault(r =>
            r.Type == RelationshipType.Implements);
        Assert.NotNull(implements);
        Assert.Contains("MyService", implements.FromFqn);
        Assert.Contains("IService", implements.ToFqn);
    }

    [Fact]
    public void ExtractsRecord()
    {
        var (symbols, _) = ExtractFromCode("""
            namespace MyApp;
            public record OrderDto(int Id, string Name);
            """);

        var rec = symbols.OfType<ClassInfo>().FirstOrDefault(s => s.Name == "OrderDto");
        Assert.NotNull(rec);
        Assert.Equal("record", rec.Kind);
    }

    [Fact]
    public void ExtractsEnum()
    {
        var (symbols, _) = ExtractFromCode("""
            namespace MyApp;
            public enum OrderStatus { Pending, Completed, Cancelled }
            """);

        var enm = symbols.OfType<ClassInfo>().FirstOrDefault(s => s.Name == "OrderStatus");
        Assert.NotNull(enm);
        Assert.Equal("enum", enm.Kind);
    }

    [Fact]
    public void ExtractsProperty()
    {
        var (symbols, _) = ExtractFromCode("""
            namespace MyApp;
            public class Order
            {
                public int Id { get; set; }
                public string Name { get; init; }
            }
            """);

        var props = symbols.OfType<PropertyInfo>().ToList();
        Assert.Contains(props, p => p.Name == "Id" && p.HasGetter && p.HasSetter);
        Assert.Contains(props, p => p.Name == "Name" && p.HasGetter);
    }

    [Fact]
    public void ExtractsHasMethodRelationship()
    {
        var (_, relationships) = ExtractFromCode("""
            namespace MyApp;
            public class Calculator
            {
                public int Add(int a, int b) => a + b;
            }
            """);

        var hasMethod = relationships.FirstOrDefault(r =>
            r.Type == RelationshipType.HasMethod && r.ToFqn.Contains("Add"));
        Assert.NotNull(hasMethod);
        Assert.Contains("Calculator", hasMethod.FromFqn);
    }

    // --- Helper ---

    private static (IReadOnlyList<CodeSymbol> Symbols, IReadOnlyList<Relationship> Relationships) ExtractFromCode(string code)
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

        var extractor = new SymbolExtractor(semanticModel);
        extractor.Visit(root);

        return (extractor.Symbols, extractor.Relationships);
    }
}
