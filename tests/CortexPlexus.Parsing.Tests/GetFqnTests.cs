using CortexPlexus.Core.Models;
using CortexPlexus.Parsing.Extractors;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CortexPlexus.Parsing.Tests;

/// <summary>
/// Tests that GetFqn produces fully qualified names for all symbol types,
/// including methods, properties, generics, nested types, and constructors.
/// These tests were added after discovering that FullyQualifiedFormat
/// does not qualify members — only types.
/// </summary>
public sealed class GetFqnTests
{
    [Fact]
    public void Method_IncludesContainingType()
    {
        var (symbols, _) = ExtractFromCode("""
            namespace MyApp;
            public class OrderService
            {
                public void Process() { }
            }
            """);

        var method = symbols.OfType<MethodInfo>().First(s => s.Name == "Process");
        Assert.Equal("MyApp.OrderService.Process", method.Fqn);
    }

    [Fact]
    public void Property_IncludesContainingType()
    {
        var (symbols, _) = ExtractFromCode("""
            namespace MyApp;
            public class Order
            {
                public int Total { get; set; }
            }
            """);

        var prop = symbols.OfType<PropertyInfo>().First(s => s.Name == "Total");
        Assert.Equal("MyApp.Order.Total", prop.Fqn);
    }

    [Fact]
    public void Constructor_IncludesContainingType()
    {
        var (symbols, _) = ExtractFromCode("""
            namespace MyApp;
            public class OrderService
            {
                public OrderService() { }
            }
            """);

        var ctor = symbols.OfType<ConstructorInfo>().First();
        Assert.Contains("MyApp.OrderService", ctor.Fqn);
    }

    [Fact]
    public void GenericClass_IncludesTypeParameter()
    {
        var (symbols, _) = ExtractFromCode("""
            namespace MyApp;
            public class Repository<T> { }
            """);

        var cls = symbols.OfType<ClassInfo>().First(s => s.Name == "Repository");
        Assert.Contains("Repository<T>", cls.Fqn);
    }

    [Fact]
    public void NestedClass_IncludesOuterType()
    {
        var (symbols, _) = ExtractFromCode("""
            namespace MyApp;
            public class Outer
            {
                public class Inner { }
            }
            """);

        var inner = symbols.OfType<ClassInfo>().First(s => s.Name == "Inner");
        Assert.Equal("MyApp.Outer.Inner", inner.Fqn);
    }

    [Fact]
    public void MethodInNestedClass_FullyQualified()
    {
        var (symbols, _) = ExtractFromCode("""
            namespace MyApp;
            public class Outer
            {
                public class Inner
                {
                    public void DoWork() { }
                }
            }
            """);

        var method = symbols.OfType<MethodInfo>().First(s => s.Name == "DoWork");
        Assert.Equal("MyApp.Outer.Inner.DoWork", method.Fqn);
    }

    [Fact]
    public void InterfaceMethod_FullyQualified()
    {
        var (symbols, _) = ExtractFromCode("""
            namespace MyApp;
            public interface IService
            {
                void Execute();
            }
            """);

        var iface = symbols.OfType<InterfaceInfo>().First(s => s.Name == "IService");
        Assert.Contains("MyApp.IService.Execute", iface.MemberFqns);
    }

    [Fact]
    public void EmptyFqn_SkippedFromSymbols()
    {
        // All extracted symbols should have non-empty FQN
        var (symbols, relationships) = ExtractFromCode("""
            namespace MyApp;
            public class Svc
            {
                public void Run() { }
            }
            """);

        Assert.All(symbols, s => Assert.False(string.IsNullOrWhiteSpace(s.Fqn),
            $"Symbol {s.Name} (kind={s.Kind}) has empty FQN"));
        Assert.All(relationships, r =>
        {
            Assert.False(string.IsNullOrWhiteSpace(r.FromFqn), "Relationship has empty FromFqn");
            Assert.False(string.IsNullOrWhiteSpace(r.ToFqn), "Relationship has empty ToFqn");
        });
    }

    [Fact]
    public void HasMethod_Relationship_UsesFullFqn()
    {
        var (_, relationships) = ExtractFromCode("""
            namespace MyApp;
            public class Svc
            {
                public void Run() { }
            }
            """);

        var hasMethod = relationships.First(r => r.Type == RelationshipType.HasMethod);
        Assert.Equal("MyApp.Svc", hasMethod.FromFqn);
        Assert.Equal("MyApp.Svc.Run", hasMethod.ToFqn);
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
