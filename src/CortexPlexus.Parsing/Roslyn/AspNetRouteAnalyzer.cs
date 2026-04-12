using CortexPlexus.Core.Models;
using CortexPlexus.Parsing.Extractors;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CortexPlexus.Parsing.Roslyn;

internal sealed record RouteAnalysisResult(
    IReadOnlyList<ApiEndpointInfo> Endpoints,
    IReadOnlyList<Relationship> Relationships
);

internal sealed class AspNetRouteAnalyzer : CSharpSyntaxWalker
{
    private static readonly HashSet<string> MapMethods = new(StringComparer.Ordinal)
    {
        "MapGet", "MapPost", "MapPut", "MapDelete", "MapPatch"
    };

    private static readonly Dictionary<string, string> MethodToHttpVerb = new(StringComparer.Ordinal)
    {
        ["MapGet"] = "GET",
        ["MapPost"] = "POST",
        ["MapPut"] = "PUT",
        ["MapDelete"] = "DELETE",
        ["MapPatch"] = "PATCH",
    };

    private static readonly Dictionary<string, string> HttpAttributeToVerb = new(StringComparer.Ordinal)
    {
        ["HttpGet"] = "GET",
        ["HttpPost"] = "POST",
        ["HttpPut"] = "PUT",
        ["HttpDelete"] = "DELETE",
        ["HttpPatch"] = "PATCH",
        ["HttpGetAttribute"] = "GET",
        ["HttpPostAttribute"] = "POST",
        ["HttpPutAttribute"] = "PUT",
        ["HttpDeleteAttribute"] = "DELETE",
        ["HttpPatchAttribute"] = "PATCH",
    };

    private readonly SemanticModel _semanticModel;
    private readonly List<ApiEndpointInfo> _endpoints = [];
    private readonly List<Relationship> _relationships = [];

    /// <summary>
    /// Tracks route prefixes defined via MapGroup() keyed by the variable name
    /// that receives the group builder (e.g. <c>var group = app.MapGroup("/api/tasks")</c>).
    /// </summary>
    private readonly Dictionary<string, string> _groupPrefixes = new(StringComparer.Ordinal);

    public AspNetRouteAnalyzer(SemanticModel semanticModel)
    {
        _semanticModel = semanticModel;
    }

    public RouteAnalysisResult Analyze(SyntaxNode root)
    {
        // First pass: collect MapGroup variable assignments so prefixes are
        // available when we encounter the Map* calls that reference them.
        CollectGroupPrefixes(root);

        // Second pass: walk the full tree to extract endpoints.
        Visit(root);

        return new RouteAnalysisResult(_endpoints, _relationships);
    }

    // ------------------------------------------------------------------
    //  Main visitor
    // ------------------------------------------------------------------

    public override void VisitInvocationExpression(InvocationExpressionSyntax node)
    {
        var methodName = GetInvokedMethodName(node);

        if (methodName is not null)
        {
            if (MapMethods.Contains(methodName))
            {
                ProcessMapEndpoint(node, methodName);
            }
            else if (methodName == "MapHub")
            {
                ProcessMapHub(node);
            }
        }

        base.VisitInvocationExpression(node);
    }

    // ------------------------------------------------------------------
    //  MVC Controller detection ([ApiController] + [HttpGet] etc.)
    // ------------------------------------------------------------------

    public override void VisitMethodDeclaration(MethodDeclarationSyntax node)
    {
        // Check if this method has [HttpGet], [HttpPost], etc.
        foreach (var attrList in node.AttributeLists)
        {
            foreach (var attr in attrList.Attributes)
            {
                var attrName = attr.Name.ToString();
                if (!HttpAttributeToVerb.TryGetValue(attrName, out var httpMethod))
                    continue;

                // Found an HTTP action method — extract endpoint info
                var actionRoute = ExtractAttributeArgument(attr);
                var controllerRoute = GetControllerRoute(node);

                var handlerFqn = _semanticModel.GetDeclaredSymbol(node) is IMethodSymbol methodSymbol
                    ? SymbolExtractor.GetFqn(methodSymbol)
                    : null;

                var controllerName = node.Ancestors().OfType<ClassDeclarationSyntax>().FirstOrDefault()
                    ?.Identifier.Text;
                var actionName = node.Identifier.Text;

                // ASP.NET route token replacement: [controller]/[action] resolved at compile-time.
                // Strip "Controller" suffix per ASP.NET convention (TrainerController → "Trainer").
                // Without this, raw template "api/[controller]/completion" gets stored, and
                // queries like "/api/chat/completion" never match.
                var resolvedControllerRoute = ExpandRouteTokens(controllerRoute, controllerName, actionName);
                var resolvedActionRoute = ExpandRouteTokens(actionRoute, controllerName, actionName);
                var fullRoute = CombineRoutes(resolvedControllerRoute, resolvedActionRoute ?? "");

                var filePath = node.SyntaxTree.FilePath;
                var lineSpan = node.GetLocation().GetLineSpan();
                var endpointFqn = $"API:{httpMethod}:{fullRoute}";

                _endpoints.Add(new ApiEndpointInfo
                {
                    Fqn = endpointFqn,
                    Name = $"{httpMethod} {fullRoute}",
                    Kind = "api_endpoint",
                    FilePath = filePath,
                    StartLine = lineSpan.StartLinePosition.Line + 1,
                    EndLine = lineSpan.EndLinePosition.Line + 1,
                    HttpMethod = httpMethod,
                    RouteTemplate = fullRoute,
                    HandlerMethodFqn = handlerFqn,
                    ModuleName = controllerName
                });

                if (!string.IsNullOrWhiteSpace(handlerFqn))
                    _relationships.Add(new Relationship(endpointFqn, handlerFqn, RelationshipType.HandledBy));

                break; // Only first HTTP attribute per method
            }
        }

        base.VisitMethodDeclaration(node);
    }

    private string? GetControllerRoute(MethodDeclarationSyntax method)
    {
        var classDecl = method.Ancestors().OfType<ClassDeclarationSyntax>().FirstOrDefault();
        if (classDecl is null) return null;

        foreach (var attrList in classDecl.AttributeLists)
        {
            foreach (var attr in attrList.Attributes)
            {
                var name = attr.Name.ToString();
                if (name is "Route" or "RouteAttribute")
                    return ExtractAttributeArgument(attr);
            }
        }

        return null;
    }

    private static string? ExtractAttributeArgument(AttributeSyntax attr)
    {
        if (attr.ArgumentList is null || attr.ArgumentList.Arguments.Count == 0)
            return null;

        var arg = attr.ArgumentList.Arguments[0].Expression;
        if (arg is LiteralExpressionSyntax literal)
            return literal.Token.ValueText;

        return arg.ToString().Trim('"');
    }

    // ------------------------------------------------------------------
    //  MapGet / MapPost / MapPut / MapDelete / MapPatch
    // ------------------------------------------------------------------

    private void ProcessMapEndpoint(InvocationExpressionSyntax node, string methodName)
    {
        var routeTemplate = ExtractFirstStringArgument(node);
        if (routeTemplate is null)
            return;

        var httpMethod = MethodToHttpVerb[methodName];
        var groupPrefix = ResolveGroupPrefix(node);
        var fullRoute = CombineRoutes(groupPrefix, routeTemplate);

        // Walk the fluent chain to find WithName / WithSummary.
        var (endpointName, summary) = ExtractFluentMetadata(node);

        // Try to resolve the handler.
        var handlerFqn = ResolveHandlerFqn(node);

        // Determine containing module (enclosing class name).
        var moduleName = GetEnclosingTypeName(node);

        var filePath = node.SyntaxTree.FilePath;
        var lineSpan = node.GetLocation().GetLineSpan();

        var endpointFqn = $"API:{httpMethod}:{fullRoute}";

        _endpoints.Add(new ApiEndpointInfo
        {
            Fqn = endpointFqn,
            Name = endpointName ?? $"{httpMethod} {fullRoute}",
            Kind = "api_endpoint",
            FilePath = filePath,
            StartLine = lineSpan.StartLinePosition.Line + 1,
            EndLine = lineSpan.EndLinePosition.Line + 1,
            HttpMethod = httpMethod,
            RouteTemplate = fullRoute,
            HandlerMethodFqn = handlerFqn,
            EndpointName = endpointName,
            Summary = summary,
            ModuleName = moduleName,
        });

        // Emit HandledBy edge: endpoint → handler method
        if (!string.IsNullOrWhiteSpace(handlerFqn))
        {
            _relationships.Add(new Relationship(endpointFqn, handlerFqn, RelationshipType.HandledBy));
        }
    }

    // ------------------------------------------------------------------
    //  MapHub<T>
    // ------------------------------------------------------------------

    private void ProcessMapHub(InvocationExpressionSyntax node)
    {
        var routeTemplate = ExtractFirstStringArgument(node);
        if (routeTemplate is null)
            return;

        var groupPrefix = ResolveGroupPrefix(node);
        var fullRoute = CombineRoutes(groupPrefix, routeTemplate);

        // Resolve the hub type from the generic argument: MapHub<THub>(...)
        string? hubTypeFqn = null;
        if (node.Expression is MemberAccessExpressionSyntax memberAccess
            && memberAccess.Name is GenericNameSyntax genericName
            && genericName.TypeArgumentList.Arguments.Count > 0)
        {
            var typeArg = genericName.TypeArgumentList.Arguments[0];
            var typeInfo = _semanticModel.GetTypeInfo(typeArg);
            if (typeInfo.Type is not null)
            {
                hubTypeFqn = SymbolExtractor.GetFqn(typeInfo.Type);
            }
        }

        var moduleName = GetEnclosingTypeName(node);
        var filePath = node.SyntaxTree.FilePath;
        var lineSpan = node.GetLocation().GetLineSpan();

        _endpoints.Add(new ApiEndpointInfo
        {
            Fqn = $"API:WS:{fullRoute}",
            Name = $"SignalR {fullRoute}",
            Kind = "api_endpoint",
            FilePath = filePath,
            StartLine = lineSpan.StartLinePosition.Line + 1,
            EndLine = lineSpan.EndLinePosition.Line + 1,
            HttpMethod = "WS",
            RouteTemplate = fullRoute,
            HandlerMethodFqn = hubTypeFqn,
            ModuleName = moduleName,
        });
    }

    // ------------------------------------------------------------------
    //  MapGroup prefix collection
    // ------------------------------------------------------------------

    private void CollectGroupPrefixes(SyntaxNode root)
    {
        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (GetInvokedMethodName(invocation) != "MapGroup")
                continue;

            var prefix = ExtractFirstStringArgument(invocation);
            if (prefix is null)
                continue;

            // Resolve any parent group prefix (e.g. nested groups).
            var parentPrefix = ResolveGroupPrefix(invocation);
            var fullPrefix = CombineRoutes(parentPrefix, prefix);

            // Determine the variable name that captures the group builder.
            // Pattern 1: var group = app.MapGroup("/api/tasks");
            if (invocation.Parent is EqualsValueClauseSyntax equalsValue
                && equalsValue.Parent is VariableDeclaratorSyntax declarator)
            {
                _groupPrefixes[declarator.Identifier.Text] = fullPrefix;
            }
            // Pattern 2: assignment — group = app.MapGroup("/api/tasks");
            else if (invocation.Parent is AssignmentExpressionSyntax assignment
                     && assignment.Left is IdentifierNameSyntax identifier)
            {
                _groupPrefixes[identifier.Identifier.Text] = fullPrefix;
            }
        }
    }

    /// <summary>
    /// Given an invocation like <c>group.MapGet(...)</c>, resolve the route prefix
    /// from the receiver variable by looking it up in <see cref="_groupPrefixes"/>.
    /// </summary>
    private string? ResolveGroupPrefix(InvocationExpressionSyntax node)
    {
        // The receiver is the expression before the dot: group.MapGet(...)
        if (node.Expression is MemberAccessExpressionSyntax memberAccess
            && memberAccess.Expression is IdentifierNameSyntax receiver)
        {
            if (_groupPrefixes.TryGetValue(receiver.Identifier.Text, out var prefix))
                return prefix;
        }

        return null;
    }

    // ------------------------------------------------------------------
    //  Fluent chain: WithName / WithSummary
    // ------------------------------------------------------------------

    /// <summary>
    /// Walks up the fluent chain starting from the Map* invocation to extract
    /// .WithName("...") and .WithSummary("...") values.
    /// </summary>
    private static (string? EndpointName, string? Summary) ExtractFluentMetadata(
        InvocationExpressionSyntax mapInvocation)
    {
        string? endpointName = null;
        string? summary = null;

        // The fluent chain wraps the Map* call: .WithName("X") sits as the
        // parent invocation where the Map* result is the receiver.
        // Walk upward through the chain.
        SyntaxNode current = mapInvocation;
        while (current.Parent is MemberAccessExpressionSyntax parentAccess
               && parentAccess.Parent is InvocationExpressionSyntax parentInvocation)
        {
            var name = parentAccess.Name.Identifier.Text;

            if (name == "WithName")
            {
                endpointName = ExtractFirstStringArgument(parentInvocation);
            }
            else if (name == "WithSummary")
            {
                summary = ExtractFirstStringArgument(parentInvocation);
            }

            current = parentInvocation;
        }

        return (endpointName, summary);
    }

    // ------------------------------------------------------------------
    //  Handler resolution
    // ------------------------------------------------------------------

    /// <summary>
    /// Attempts to resolve the handler delegate/method reference passed as the
    /// second argument to a Map* call.
    /// </summary>
    private string? ResolveHandlerFqn(InvocationExpressionSyntax node)
    {
        var args = node.ArgumentList.Arguments;
        if (args.Count < 2)
            return null;

        var handlerArg = args[1].Expression;

        // Case 1: static method reference — e.g. CreateTaskEndpoint.Handle
        // Case 2: method group — e.g. HandleListUsers
        var symbolInfo = _semanticModel.GetSymbolInfo(handlerArg);
        if (symbolInfo.Symbol is IMethodSymbol methodSymbol)
        {
            return SymbolExtractor.GetFqn(methodSymbol);
        }

        // Case 3: lambda — try to resolve to the containing method if
        // the lambda is trivially delegating to a single call.
        if (handlerArg is ParenthesizedLambdaExpressionSyntax or
            SimpleLambdaExpressionSyntax)
        {
            return ResolveLambdaTargetMethod(handlerArg);
        }

        return null;
    }

    /// <summary>
    /// If a lambda body is a single invocation expression, resolve the target method.
    /// </summary>
    private string? ResolveLambdaTargetMethod(ExpressionSyntax lambda)
    {
        ExpressionSyntax? body = lambda switch
        {
            ParenthesizedLambdaExpressionSyntax p => p.ExpressionBody,
            SimpleLambdaExpressionSyntax s => s.ExpressionBody,
            _ => null,
        };

        // If the body is a block, try single-statement return/expression.
        if (body is null)
        {
            var block = lambda switch
            {
                ParenthesizedLambdaExpressionSyntax p => p.Block,
                SimpleLambdaExpressionSyntax s => s.Block,
                _ => null,
            };

            if (block?.Statements.Count == 1)
            {
                body = block.Statements[0] switch
                {
                    ExpressionStatementSyntax es => es.Expression,
                    ReturnStatementSyntax rs => rs.Expression,
                    _ => null,
                };
            }
        }

        if (body is null)
            return null;

        // Unwrap await.
        if (body is AwaitExpressionSyntax awaitExpr)
            body = awaitExpr.Expression;

        if (body is InvocationExpressionSyntax invocation)
        {
            var symbol = _semanticModel.GetSymbolInfo(invocation).Symbol;
            if (symbol is IMethodSymbol target)
                return SymbolExtractor.GetFqn(target);
        }

        return null;
    }

    // ------------------------------------------------------------------
    //  Helpers
    // ------------------------------------------------------------------

    private static string? GetInvokedMethodName(InvocationExpressionSyntax node)
    {
        return node.Expression switch
        {
            MemberAccessExpressionSyntax memberAccess => memberAccess.Name switch
            {
                GenericNameSyntax generic => generic.Identifier.Text,
                _ => memberAccess.Name.Identifier.Text,
            },
            IdentifierNameSyntax identifier => identifier.Identifier.Text,
            _ => null,
        };
    }

    private static string? ExtractFirstStringArgument(InvocationExpressionSyntax node)
    {
        if (node.ArgumentList.Arguments.Count == 0)
            return null;

        var firstArg = node.ArgumentList.Arguments[0].Expression;

        // Simple string literal: "/api/tasks"
        if (firstArg is LiteralExpressionSyntax literal
            && literal.IsKind(SyntaxKind.StringLiteralExpression))
        {
            return literal.Token.ValueText;
        }

        // Interpolated string with no interpolations (effectively a constant):
        // $"/api/tasks"
        if (firstArg is InterpolatedStringExpressionSyntax interpolated
            && interpolated.Contents.All(c => c is InterpolatedStringTextSyntax))
        {
            return string.Concat(
                interpolated.Contents.OfType<InterpolatedStringTextSyntax>()
                    .Select(t => t.TextToken.ValueText));
        }

        return null;
    }

    /// <summary>
    /// Expands ASP.NET route tokens [controller] and [action] in a raw route template.
    /// - [controller] → controller class name with "Controller" suffix stripped (case-insensitive replace)
    /// - [action] → method name
    /// Standard MVC convention: <see href="https://learn.microsoft.com/aspnet/core/mvc/controllers/routing#token-replacement-in-route-templates-controller-action-area"/>
    /// Returns null if input is null. Tokens not present → no-op.
    /// </summary>
    internal static string? ExpandRouteTokens(string? template, string? controllerName, string? actionName)
    {
        if (string.IsNullOrEmpty(template))
            return template;

        var result = template;

        if (controllerName is not null)
        {
            // Strip "Controller" suffix per ASP.NET convention.
            // ChatController → "Chat", UserController → "User", BaseController → "Base"
            var controllerRouteName = controllerName.EndsWith("Controller", StringComparison.Ordinal)
                ? controllerName[..^"Controller".Length]
                : controllerName;

            // Token is case-insensitive: [controller], [Controller], [CONTROLLER]
            result = System.Text.RegularExpressions.Regex.Replace(
                result, @"\[controller\]", controllerRouteName,
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }

        if (actionName is not null)
        {
            result = System.Text.RegularExpressions.Regex.Replace(
                result, @"\[action\]", actionName,
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }

        return result;
    }

    private static string CombineRoutes(string? prefix, string route)
    {
        if (string.IsNullOrEmpty(prefix))
            return route;

        // Normalize: strip trailing slash from prefix and leading slash from route
        // so we don't get double slashes.
        var normalizedPrefix = prefix.TrimEnd('/');
        var normalizedRoute = route.TrimStart('/');

        if (string.IsNullOrEmpty(normalizedRoute))
            return normalizedPrefix;

        return $"{normalizedPrefix}/{normalizedRoute}";
    }

    private string? GetEnclosingTypeName(SyntaxNode node)
    {
        var typeDecl = node.Ancestors()
            .OfType<TypeDeclarationSyntax>()
            .FirstOrDefault();

        if (typeDecl is null)
            return null;

        var symbol = _semanticModel.GetDeclaredSymbol(typeDecl);
        return symbol?.Name;
    }
}
