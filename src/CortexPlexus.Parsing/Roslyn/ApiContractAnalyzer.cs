using CortexPlexus.Core.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CortexPlexus.Parsing.Roslyn;

internal sealed record ApiContractResult(
    IReadOnlyList<Relationship> Relationships
);

/// <summary>
/// Analyzes API endpoint handlers to extract request/response DTO contracts.
/// For each method that handles an API endpoint:
/// - Parameters (excluding special types) → AcceptsDto edge
/// - Return type (unwrapped from Task, ActionResult, IResult) → ReturnsDto edge
/// Works with both Minimal API handlers and Controller action methods.
/// </summary>
internal sealed class ApiContractAnalyzer
{
    private static readonly HashSet<string> SkipParameterTypes = new(StringComparer.Ordinal)
    {
        "System.Threading.CancellationToken",
        "Microsoft.AspNetCore.Http.HttpContext",
        "Microsoft.AspNetCore.Http.HttpRequest",
        "Microsoft.AspNetCore.Http.HttpResponse",
        "System.Security.Claims.ClaimsPrincipal",
    };

    private static readonly HashSet<string> WrapperTypes = new(StringComparer.Ordinal)
    {
        "System.Threading.Tasks.Task",
        "System.Threading.Tasks.ValueTask",
        "Microsoft.AspNetCore.Mvc.ActionResult",
        "Microsoft.AspNetCore.Mvc.IActionResult",
        "Microsoft.AspNetCore.Http.IResult",
    };

    private static readonly HashSet<string> ControllerAttributes = new(StringComparer.Ordinal)
    {
        "HttpGet", "HttpPost", "HttpPut", "HttpDelete", "HttpPatch",
        "HttpGetAttribute", "HttpPostAttribute", "HttpPutAttribute", "HttpDeleteAttribute", "HttpPatchAttribute",
    };

    private readonly SemanticModel _semanticModel;
    private readonly List<Relationship> _relationships = [];
    private readonly HashSet<string> _seen = [];

    public ApiContractAnalyzer(SemanticModel semanticModel)
    {
        _semanticModel = semanticModel;
    }

    public ApiContractResult Analyze(SyntaxNode root)
    {
        // Find controller action methods
        foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            if (_semanticModel.GetDeclaredSymbol(method) is not IMethodSymbol symbol)
                continue;

            if (!IsApiEndpointMethod(symbol))
                continue;

            var endpointFqn = Extractors.SymbolExtractor.GetFqn(symbol);
            ExtractContracts(symbol, endpointFqn);
        }

        return new ApiContractResult(_relationships);
    }

    private void ExtractContracts(IMethodSymbol method, string endpointFqn)
    {
        // Request DTOs: non-primitive, non-service parameters
        foreach (var param in method.Parameters)
        {
            var paramType = param.Type;
            var paramTypeFqn = paramType.ToDisplayString();

            if (SkipParameterTypes.Contains(paramTypeFqn))
                continue;

            // Skip primitive types and strings
            if (paramType.SpecialType != SpecialType.None)
                continue;

            // Skip DI-injected services (interfaces)
            if (paramType.TypeKind == TypeKind.Interface)
                continue;

            // This is likely a request DTO or [FromBody] model
            if (IsComplexType(paramType))
            {
                AddEdge(endpointFqn, paramTypeFqn, RelationshipType.AcceptsDto);
            }
        }

        // Response DTO: unwrap Task<T>, ActionResult<T>, etc.
        var returnType = UnwrapReturnType(method.ReturnType);
        if (returnType is not null && IsComplexType(returnType))
        {
            AddEdge(endpointFqn, returnType.ToDisplayString(), RelationshipType.ReturnsDto);
        }
    }

    private bool IsApiEndpointMethod(IMethodSymbol method)
    {
        // Check for [HttpGet], [HttpPost], etc. attributes
        foreach (var attr in method.GetAttributes())
        {
            if (attr.AttributeClass?.Name is not null && ControllerAttributes.Contains(attr.AttributeClass.Name))
                return true;
        }

        // Check if containing type inherits from ControllerBase
        var containingType = method.ContainingType;
        while (containingType is not null)
        {
            var baseTypeFqn = containingType.BaseType?.ToDisplayString() ?? "";
            if (baseTypeFqn.Contains("ControllerBase") || baseTypeFqn.Contains("Controller"))
                return true;
            containingType = containingType.BaseType;
        }

        return false;
    }

    private static ITypeSymbol? UnwrapReturnType(ITypeSymbol type)
    {
        // Unwrap Task<T>, ValueTask<T>, ActionResult<T>
        if (type is INamedTypeSymbol namedType && namedType.IsGenericType)
        {
            var originalDef = namedType.OriginalDefinition.ToDisplayString();
            if (WrapperTypes.Any(w => originalDef.StartsWith(w, StringComparison.Ordinal)) &&
                namedType.TypeArguments.Length == 1)
            {
                return UnwrapReturnType(namedType.TypeArguments[0]);
            }
        }

        // Skip void, Task (non-generic), IResult, IActionResult
        var fqn = type.ToDisplayString();
        if (type.SpecialType == SpecialType.System_Void ||
            fqn == "System.Threading.Tasks.Task" ||
            WrapperTypes.Contains(fqn))
            return null;

        return type;
    }

    private static bool IsComplexType(ITypeSymbol type)
    {
        // Complex = class/struct/record that's not a system type
        if (type.SpecialType != SpecialType.None) return false;
        if (type.TypeKind is not (TypeKind.Class or TypeKind.Struct)) return false;

        var fqn = type.ToDisplayString();
        if (fqn.StartsWith("System.", StringComparison.Ordinal) && !fqn.Contains("Collections")) return false;
        if (fqn.StartsWith("Microsoft.AspNetCore.", StringComparison.Ordinal)) return false;
        if (fqn == "string" || fqn == "object") return false;

        return true;
    }

    private void AddEdge(string fromFqn, string toFqn, RelationshipType type)
    {
        var key = $"{fromFqn}->{toFqn}:{type}";
        if (!_seen.Add(key)) return;
        _relationships.Add(new Relationship(fromFqn, toFqn, type));
    }
}
