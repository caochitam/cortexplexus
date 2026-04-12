using CortexPlexus.Core.Models;
using CortexPlexus.Parsing.Extractors;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CortexPlexus.Parsing.Tests;

// ============================================================
// Field Extraction Tests
// ============================================================

public sealed class FieldExtractorTests
{
    [Fact]
    public void ExtractsPublicField()
    {
        var (symbols, relationships) = ExtractFromCode("""
            namespace MyApp;
            public class Config
            {
                public string ConnectionString = "default";
            }
            """);

        var field = symbols.OfType<FieldInfo>().FirstOrDefault(s => s.Name == "ConnectionString");
        Assert.NotNull(field);
        Assert.Equal("field", field.Kind);
        Assert.Equal("string", field.Type);
        Assert.Equal("public", field.Accessibility);
        Assert.False(field.IsConst);
        Assert.False(field.IsReadOnly);
        Assert.Contains(relationships, r => r.Type == RelationshipType.HasField && r.ToFqn.Contains("ConnectionString"));
    }

    [Fact]
    public void ExtractsReadonlyField()
    {
        var (symbols, _) = ExtractFromCode("""
            namespace MyApp;
            public class Service
            {
                private readonly int _timeout = 30;
            }
            """);

        var field = symbols.OfType<FieldInfo>().FirstOrDefault(s => s.Name == "_timeout");
        Assert.NotNull(field);
        Assert.True(field.IsReadOnly);
        Assert.Equal("private", field.Accessibility);
        Assert.Equal("int", field.Type);
    }

    [Fact]
    public void ExtractsConstField()
    {
        var (symbols, _) = ExtractFromCode("""
            namespace MyApp;
            public class Constants
            {
                public const int MaxRetries = 3;
                public const string DefaultName = "test";
            }
            """);

        var maxRetries = symbols.OfType<FieldInfo>().FirstOrDefault(s => s.Name == "MaxRetries");
        Assert.NotNull(maxRetries);
        Assert.True(maxRetries.IsConst);
        Assert.Equal("const", maxRetries.Kind);
        Assert.Equal("3", maxRetries.ConstantValue);

        var defaultName = symbols.OfType<FieldInfo>().FirstOrDefault(s => s.Name == "DefaultName");
        Assert.NotNull(defaultName);
        Assert.Equal("test", defaultName.ConstantValue);
    }

    [Fact]
    public void ExtractsStaticField()
    {
        var (symbols, _) = ExtractFromCode("""
            namespace MyApp;
            public class Cache
            {
                private static readonly object _lock = new object();
            }
            """);

        var field = symbols.OfType<FieldInfo>().FirstOrDefault(s => s.Name == "_lock");
        Assert.NotNull(field);
        Assert.True(field.IsStatic);
        Assert.True(field.IsReadOnly);
    }

    [Fact]
    public void SkipsBackingFields()
    {
        var (symbols, _) = ExtractFromCode("""
            namespace MyApp;
            public class Dto
            {
                public string Name { get; set; }
            }
            """);

        // Auto-property backing field should NOT appear as FieldInfo
        Assert.Empty(symbols.OfType<FieldInfo>());
    }

    [Fact]
    public void ExtractsMultipleFieldsInSingleDeclaration()
    {
        var (symbols, _) = ExtractFromCode("""
            namespace MyApp;
            public class Point
            {
                public int X, Y, Z;
            }
            """);

        var fields = symbols.OfType<FieldInfo>().ToList();
        Assert.Equal(3, fields.Count);
        Assert.Contains(fields, f => f.Name == "X");
        Assert.Contains(fields, f => f.Name == "Y");
        Assert.Contains(fields, f => f.Name == "Z");
    }

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

// ============================================================
// Event Extraction Tests
// ============================================================

public sealed class EventExtractorTests
{
    [Fact]
    public void ExtractsEventField()
    {
        var (symbols, relationships) = ExtractFromCode("""
            using System;
            namespace MyApp;
            public class OrderService
            {
                public event EventHandler OrderCreated;
            }
            """);

        var evt = symbols.OfType<EventInfo>().FirstOrDefault(s => s.Name == "OrderCreated");
        Assert.NotNull(evt);
        Assert.Equal("event", evt.Kind);
        Assert.Equal("public", evt.Accessibility);
        Assert.Contains("EventHandler", evt.Type);
        Assert.Contains(relationships, r => r.Type == RelationshipType.HasEvent && r.ToFqn.Contains("OrderCreated"));
    }

    [Fact]
    public void ExtractsGenericEventHandler()
    {
        var (symbols, _) = ExtractFromCode("""
            using System;
            namespace MyApp;
            public class EventArgs<T> : EventArgs { public T Data { get; init; } }
            public class Bus
            {
                public event EventHandler<EventArgs<string>> MessageReceived;
            }
            """);

        var evt = symbols.OfType<EventInfo>().FirstOrDefault(s => s.Name == "MessageReceived");
        Assert.NotNull(evt);
        Assert.Contains("EventHandler", evt.Type);
    }

    [Fact]
    public void ExtractsStaticEvent()
    {
        var (symbols, _) = ExtractFromCode("""
            using System;
            namespace MyApp;
            public class AppDomain
            {
                public static event EventHandler ProcessExit;
            }
            """);

        var evt = symbols.OfType<EventInfo>().FirstOrDefault(s => s.Name == "ProcessExit");
        Assert.NotNull(evt);
        Assert.True(evt.IsStatic);
    }

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

// ============================================================
// Event Subscription/Publish Detection Tests
// ============================================================

public sealed class EventSubscriptionTests
{
    [Fact]
    public void DetectsEventSubscription()
    {
        var relationships = ExtractCallsFromCode("""
            using System;
            namespace MyApp;
            public class OrderService
            {
                public event EventHandler OrderCreated;
            }
            public class NotificationService
            {
                public void Subscribe(OrderService orders)
                {
                    orders.OrderCreated += OnOrderCreated;
                }
                private void OnOrderCreated(object sender, EventArgs e) { }
            }
            """);

        Assert.Contains(relationships, r => r.Type == RelationshipType.Subscribes && r.ToFqn.Contains("OrderCreated"));
    }

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
