using CortexPlexus.Core.Models;
using CortexPlexus.Parsing.Roslyn;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CortexPlexus.Parsing.Tests;

public sealed class ApiContractTests
{
    [Fact]
    public void DetectsRequestDto()
    {
        var result = AnalyzeContracts("""
            using System.Threading.Tasks;
            namespace MyApp;

            public class CreateOrderRequest { public string Item { get; set; } }

            [HttpPost]
            public class OrderController : ControllerBase
            {
                public Task<string> CreateOrder(CreateOrderRequest request) => Task.FromResult("");
            }

            public class HttpPostAttribute : System.Attribute { }
            public class ControllerBase { }
            """);

        Assert.Contains(result.Relationships, r =>
            r.Type == RelationshipType.AcceptsDto &&
            r.ToFqn.Contains("CreateOrderRequest"));
    }

    [Fact]
    public void DetectsResponseDto()
    {
        var result = AnalyzeContracts("""
            using System.Threading.Tasks;
            namespace MyApp;

            public class OrderResponse { public int Id { get; set; } }

            [HttpGet]
            public class OrderController : ControllerBase
            {
                public Task<OrderResponse> GetOrder(int id) => Task.FromResult(new OrderResponse());
            }

            public class HttpGetAttribute : System.Attribute { }
            public class ControllerBase { }
            """);

        Assert.Contains(result.Relationships, r =>
            r.Type == RelationshipType.ReturnsDto &&
            r.ToFqn.Contains("OrderResponse"));
    }

    [Fact]
    public void SkipsPrimitiveParameters()
    {
        var result = AnalyzeContracts("""
            using System.Threading.Tasks;
            namespace MyApp;

            [HttpGet]
            public class OrderController : ControllerBase
            {
                public Task<string> GetOrder(int id) => Task.FromResult("");
            }

            public class HttpGetAttribute : System.Attribute { }
            public class ControllerBase { }
            """);

        Assert.DoesNotContain(result.Relationships, r => r.Type == RelationshipType.AcceptsDto);
    }

    private static ApiContractResult AnalyzeContracts(string code)
    {
        var tree = CSharpSyntaxTree.ParseText(code);
        var compilation = CSharpCompilation.Create("TestAssembly",
            [tree],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
             MetadataReference.CreateFromFile(typeof(Task).Assembly.Location),
             MetadataReference.CreateFromFile(typeof(System.Runtime.AssemblyTargetedPatchBandAttribute).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var semanticModel = compilation.GetSemanticModel(tree);
        var analyzer = new ApiContractAnalyzer(semanticModel);
        return analyzer.Analyze(tree.GetRoot());
    }
}
