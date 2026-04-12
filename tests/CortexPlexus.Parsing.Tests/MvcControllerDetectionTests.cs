using CortexPlexus.Core.Models;
using CortexPlexus.Parsing.Roslyn;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CortexPlexus.Parsing.Tests;

public sealed class MvcControllerDetectionTests
{
    [Fact]
    public void DetectsHttpGet()
    {
        var (endpoints, _) = AnalyzeCode("""
            using Microsoft.AspNetCore.Mvc;
            namespace MyApp;
            [ApiController]
            [Route("api/orders")]
            public class OrderController : ControllerBase
            {
                [HttpGet]
                public IActionResult GetAll() => null;
            }
            """);

        var ep = endpoints.FirstOrDefault(e => e.HttpMethod == "GET");
        Assert.NotNull(ep);
        Assert.Equal("api/orders", ep.RouteTemplate);
    }

    [Fact]
    public void DetectsHttpPostWithSubRoute()
    {
        var (endpoints, _) = AnalyzeCode("""
            using Microsoft.AspNetCore.Mvc;
            namespace MyApp;
            [ApiController]
            [Route("api/ingest")]
            public class IngestController : ControllerBase
            {
                [HttpPost("batch")]
                public IActionResult PostBatch() => null;
            }
            """);

        var ep = endpoints.FirstOrDefault(e => e.HttpMethod == "POST");
        Assert.NotNull(ep);
        Assert.Equal("api/ingest/batch", ep.RouteTemplate);
    }

    [Fact]
    public void DetectsMultipleActions()
    {
        var (endpoints, _) = AnalyzeCode("""
            using Microsoft.AspNetCore.Mvc;
            namespace MyApp;
            [ApiController]
            [Route("api/items")]
            public class ItemController : ControllerBase
            {
                [HttpGet]
                public IActionResult List() => null;
                [HttpGet("{id}")]
                public IActionResult GetById(int id) => null;
                [HttpPost]
                public IActionResult Create() => null;
                [HttpDelete("{id}")]
                public IActionResult Delete(int id) => null;
            }
            """);

        Assert.Equal(4, endpoints.Count);
        Assert.Contains(endpoints, e => e.HttpMethod == "GET" && e.RouteTemplate == "api/items");
        Assert.Contains(endpoints, e => e.HttpMethod == "GET" && e.RouteTemplate == "api/items/{id}");
        Assert.Contains(endpoints, e => e.HttpMethod == "POST" && e.RouteTemplate == "api/items");
        Assert.Contains(endpoints, e => e.HttpMethod == "DELETE" && e.RouteTemplate == "api/items/{id}");
    }

    [Fact]
    public void EmitsHandledByRelationship()
    {
        var (_, relationships) = AnalyzeCode("""
            using Microsoft.AspNetCore.Mvc;
            namespace MyApp;
            [ApiController]
            [Route("api/test")]
            public class TestController : ControllerBase
            {
                [HttpGet]
                public IActionResult Get() => null;
            }
            """);

        var handledBy = relationships.FirstOrDefault(r => r.Type == RelationshipType.HandledBy);
        Assert.NotNull(handledBy);
        Assert.Contains("API:GET:api/test", handledBy.FromFqn);
    }

    // === Bug fix: [controller] token expansion (Issue #2-3 from smoke test) ===
    [Fact]
    public void RouteToken_Controller_ExpandsToClassNameWithoutSuffix()
    {
        // Mục đích: [controller] token phải resolve về class name (strip "Controller" suffix).
        // Trước fix: route stored as "api/[controller]/completion" → query "/api/chat/completion" miss.
        // Sau fix: route stored as "api/Chat/completion" → match.
        var (endpoints, _) = AnalyzeCode("""
            using Microsoft.AspNetCore.Mvc;
            namespace MyApp;
            [ApiController]
            [Route("api/[controller]")]
            public class ChatController : ControllerBase
            {
                [HttpPost("completion")]
                public IActionResult Completion() => null;
            }
            """);

        var ep = endpoints.FirstOrDefault(e => e.HttpMethod == "POST");
        Assert.NotNull(ep);
        Assert.Equal("api/Chat/completion", ep.RouteTemplate);
        Assert.DoesNotContain("[controller]", ep.RouteTemplate);
    }

    [Fact]
    public void RouteToken_ControllerCaseInsensitive_StripsSuffix()
    {
        // Token replacement phải case-insensitive theo ASP.NET convention.
        var (endpoints, _) = AnalyzeCode("""
            using Microsoft.AspNetCore.Mvc;
            namespace MyApp;
            [ApiController]
            [Route("v1/[CONTROLLER]")]
            public class TrainerController : ControllerBase
            {
                [HttpGet]
                public IActionResult List() => null;
            }
            """);

        var ep = endpoints.FirstOrDefault();
        Assert.NotNull(ep);
        Assert.Equal("v1/Trainer", ep.RouteTemplate);
    }

    [Fact]
    public void RouteToken_Action_ExpandsToMethodName()
    {
        // [action] token resolves về method name.
        var (endpoints, _) = AnalyzeCode("""
            using Microsoft.AspNetCore.Mvc;
            namespace MyApp;
            [ApiController]
            [Route("api/[controller]/[action]")]
            public class UserController : ControllerBase
            {
                [HttpGet]
                public IActionResult GetProfile() => null;
            }
            """);

        var ep = endpoints.FirstOrDefault();
        Assert.NotNull(ep);
        Assert.Equal("api/User/GetProfile", ep.RouteTemplate);
    }

    [Fact]
    public void RouteToken_NoControllerSuffix_UsesClassNameAsIs()
    {
        // Edge case: class không có "Controller" suffix → dùng nguyên class name.
        var (endpoints, _) = AnalyzeCode("""
            using Microsoft.AspNetCore.Mvc;
            namespace MyApp;
            [ApiController]
            [Route("api/[controller]")]
            public class HealthEndpoint : ControllerBase
            {
                [HttpGet]
                public IActionResult Check() => null;
            }
            """);

        var ep = endpoints.FirstOrDefault();
        Assert.NotNull(ep);
        Assert.Equal("api/HealthEndpoint", ep.RouteTemplate);
    }

    // === ExpandRouteTokens helper (pure function tests) ===
    [Theory]
    [InlineData("api/[controller]", "ChatController", null, "api/Chat")]
    [InlineData("api/[controller]/completion", "ChatController", null, "api/Chat/completion")]
    [InlineData("api/[controller]", "TrainerController", null, "api/Trainer")]
    [InlineData("v1/[CONTROLLER]", "OrderController", null, "v1/Order")]  // case-insensitive
    [InlineData("[controller]/[action]", "UserController", "Login", "User/Login")]
    [InlineData("api/orders", "OrderController", null, "api/orders")]  // no token = no-op
    [InlineData("[controller]", "Health", null, "Health")]  // no Controller suffix
    [InlineData(null, "ChatController", null, null)]  // null in = null out
    public void ExpandRouteTokens_HandlesAllCases(string? template, string? controller, string? action, string? expected)
    {
        var result = AspNetRouteAnalyzer.ExpandRouteTokens(template, controller, action);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void NoRouteAttribute_UsesEmptyPrefix()
    {
        var (endpoints, _) = AnalyzeCode("""
            using Microsoft.AspNetCore.Mvc;
            namespace MyApp;
            [ApiController]
            public class SimpleController : ControllerBase
            {
                [HttpGet("health")]
                public IActionResult Health() => null;
            }
            """);

        var ep = endpoints.FirstOrDefault();
        Assert.NotNull(ep);
        Assert.Equal("health", ep.RouteTemplate);
    }

    // --- Helper ---

    private static (IReadOnlyList<ApiEndpointInfo> Endpoints, IReadOnlyList<Relationship> Relationships) AnalyzeCode(string code)
    {
        // Add stub attribute definitions so Roslyn can parse [HttpGet], [Route], etc.
        // without requiring actual ASP.NET MVC package references
        var stubCode = """
            namespace Microsoft.AspNetCore.Mvc
            {
                public class ControllerBase { }
                public class IActionResult { }
                public class ApiControllerAttribute : System.Attribute { }
                public class RouteAttribute : System.Attribute
                {
                    public RouteAttribute(string template) { }
                }
                public class HttpGetAttribute : System.Attribute
                {
                    public HttpGetAttribute() { }
                    public HttpGetAttribute(string template) { }
                }
                public class HttpPostAttribute : System.Attribute
                {
                    public HttpPostAttribute() { }
                    public HttpPostAttribute(string template) { }
                }
                public class HttpPutAttribute : System.Attribute
                {
                    public HttpPutAttribute() { }
                    public HttpPutAttribute(string template) { }
                }
                public class HttpDeleteAttribute : System.Attribute
                {
                    public HttpDeleteAttribute() { }
                    public HttpDeleteAttribute(string template) { }
                }
            }
            """;

        var codeTree = CSharpSyntaxTree.ParseText(code);
        var stubTree = CSharpSyntaxTree.ParseText(stubCode);

        var compilation = CSharpCompilation.Create("TestAssembly",
            [codeTree, stubTree],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
             MetadataReference.CreateFromFile(typeof(System.Runtime.AssemblyTargetedPatchBandAttribute).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var semanticModel = compilation.GetSemanticModel(codeTree);
        var analyzer = new AspNetRouteAnalyzer(semanticModel);
        var result = analyzer.Analyze(codeTree.GetRoot());

        return (result.Endpoints, result.Relationships);
    }
}
