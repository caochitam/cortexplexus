using CortexPlexus.Core.Models;
using CortexPlexus.Parsing.Extractors;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CortexPlexus.Parsing.Tests;

public sealed class CodeMetricsTests
{
    [Fact]
    public void SimpleMethod_Complexity1()
    {
        var method = ExtractMethod("""
            namespace MyApp;
            public class Svc
            {
                public int Add(int a, int b)
                {
                    return a + b;
                }
            }
            """, "Add");

        Assert.NotNull(method);
        Assert.Equal(1, method.CyclomaticComplexity);
        Assert.Equal(0, method.MaxNestingDepth);
    }

    [Fact]
    public void IfElse_Complexity2()
    {
        var method = ExtractMethod("""
            namespace MyApp;
            public class Svc
            {
                public string Check(int x)
                {
                    if (x > 0)
                        return "positive";
                    else
                        return "non-positive";
                }
            }
            """, "Check");

        Assert.NotNull(method);
        Assert.Equal(2, method.CyclomaticComplexity); // 1 base + 1 if
        Assert.Equal(1, method.MaxNestingDepth);
    }

    [Fact]
    public void NestedIfForLoop_HighComplexity()
    {
        var method = ExtractMethod("""
            using System.Collections.Generic;
            namespace MyApp;
            public class Svc
            {
                public int Process(List<int> items)
                {
                    int sum = 0;
                    foreach (var item in items)
                    {
                        if (item > 0)
                        {
                            if (item < 100)
                            {
                                sum += item;
                            }
                        }
                    }
                    return sum;
                }
            }
            """, "Process");

        Assert.NotNull(method);
        Assert.Equal(4, method.CyclomaticComplexity); // 1 + foreach + if + if
        Assert.Equal(3, method.MaxNestingDepth); // foreach > if > if
    }

    [Fact]
    public void SwitchStatement_ComplexityPerCase()
    {
        var method = ExtractMethod("""
            namespace MyApp;
            public class Svc
            {
                public string GetLabel(int code)
                {
                    switch (code)
                    {
                        case 1: return "one";
                        case 2: return "two";
                        case 3: return "three";
                        default: return "unknown";
                    }
                }
            }
            """, "GetLabel");

        Assert.NotNull(method);
        Assert.Equal(5, method.CyclomaticComplexity); // 1 + 4 cases
    }

    [Fact]
    public void LogicalOperators_AddComplexity()
    {
        var method = ExtractMethod("""
            namespace MyApp;
            public class Svc
            {
                public bool Validate(int x, int y)
                {
                    return x > 0 && y > 0 || x == y;
                }
            }
            """, "Validate");

        Assert.NotNull(method);
        Assert.Equal(3, method.CyclomaticComplexity); // 1 + && + ||
    }

    [Fact]
    public void TernaryOperator_AddsComplexity()
    {
        var method = ExtractMethod("""
            namespace MyApp;
            public class Svc
            {
                public string Label(bool active)
                {
                    return active ? "on" : "off";
                }
            }
            """, "Label");

        Assert.NotNull(method);
        Assert.Equal(2, method.CyclomaticComplexity); // 1 + ternary
    }

    [Fact]
    public void TryCatch_NestingAndComplexity()
    {
        var method = ExtractMethod("""
            using System;
            namespace MyApp;
            public class Svc
            {
                public void Do()
                {
                    try
                    {
                        Console.WriteLine();
                    }
                    catch (Exception)
                    {
                        Console.WriteLine("err");
                    }
                }
            }
            """, "Do");

        Assert.NotNull(method);
        Assert.Equal(2, method.CyclomaticComplexity); // 1 + catch
        Assert.True(method.MaxNestingDepth >= 1); // try or catch
    }

    [Fact]
    public void LineCount_IsCalculated()
    {
        var method = ExtractMethod("""
            namespace MyApp;
            public class Svc
            {
                public void Multi()
                {
                    var a = 1;
                    var b = 2;
                    var c = a + b;
                }
            }
            """, "Multi");

        Assert.NotNull(method);
        Assert.NotNull(method.LineCount);
        Assert.True(method.LineCount >= 4);
    }

    private static MethodInfo? ExtractMethod(string code, string methodName)
    {
        var tree = CSharpSyntaxTree.ParseText(code);
        var compilation = CSharpCompilation.Create("TestAssembly",
            [tree],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
             MetadataReference.CreateFromFile(typeof(Task).Assembly.Location),
             MetadataReference.CreateFromFile(typeof(Console).Assembly.Location),
             MetadataReference.CreateFromFile(typeof(System.Collections.Generic.List<>).Assembly.Location),
             MetadataReference.CreateFromFile(typeof(System.Runtime.AssemblyTargetedPatchBandAttribute).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var semanticModel = compilation.GetSemanticModel(tree);
        var root = tree.GetRoot();

        var extractor = new SymbolExtractor(semanticModel);
        extractor.Visit(root);

        return extractor.Symbols.OfType<MethodInfo>().FirstOrDefault(m => m.Name == methodName);
    }
}
