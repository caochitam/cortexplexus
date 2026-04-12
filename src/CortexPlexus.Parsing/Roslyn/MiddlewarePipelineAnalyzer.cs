using CortexPlexus.Core.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CortexPlexus.Parsing.Roslyn;

internal sealed record MiddlewarePipelineResult(
    IReadOnlyList<MiddlewareInfo> Middlewares,
    IReadOnlyList<Relationship> Relationships
);

/// <summary>
/// Extracts ASP.NET middleware pipeline order from app.UseXxx() calls.
/// Detects calls like: app.UseAuthentication(), app.UseAuthorization(), app.UseMiddleware&lt;T&gt;(), etc.
/// Creates MiddlewareInfo symbols and PipelineOrder edges (middleware[n] → middleware[n+1]).
/// </summary>
internal sealed class MiddlewarePipelineAnalyzer : CSharpSyntaxWalker
{
    private readonly SemanticModel _semanticModel;
    private readonly List<(string Name, string Fqn, int Line)> _pipeline = [];

    public MiddlewarePipelineAnalyzer(SemanticModel semanticModel)
    {
        _semanticModel = semanticModel;
    }

    public MiddlewarePipelineResult Analyze(SyntaxNode root)
    {
        Visit(root);

        var middlewares = new List<MiddlewareInfo>();
        var relationships = new List<Relationship>();

        for (var i = 0; i < _pipeline.Count; i++)
        {
            var (name, fqn, line) = _pipeline[i];
            middlewares.Add(new MiddlewareInfo
            {
                Fqn = $"middleware:{name}",
                Name = name,
                Kind = "middleware",
                FilePath = root.SyntaxTree.FilePath,
                StartLine = line,
                Order = i,
            });

            // PipelineOrder edge: middleware[i] → middleware[i+1]
            if (i + 1 < _pipeline.Count)
            {
                relationships.Add(new Relationship(
                    $"middleware:{name}",
                    $"middleware:{_pipeline[i + 1].Name}",
                    RelationshipType.PipelineOrder,
                    new Dictionary<string, string>
                    {
                        ["fromOrder"] = i.ToString(),
                        ["toOrder"] = (i + 1).ToString(),
                    }));
            }
        }

        return new MiddlewarePipelineResult(middlewares, relationships);
    }

    public override void VisitInvocationExpression(InvocationExpressionSyntax node)
    {
        if (node.Expression is MemberAccessExpressionSyntax memberAccess)
        {
            var methodName = memberAccess.Name.Identifier.Text;

            // Quick syntactic filter: must start with "Use" (cheap before semantic work).
            if (methodName.StartsWith("Use", StringComparison.Ordinal) &&
                methodName.Length > 3 &&
                IsAspNetMiddlewareCall(node))
            {
                var displayName = methodName;

                // UseMiddleware<T>() — extract type name
                if (methodName == "UseMiddleware" &&
                    memberAccess.Name is GenericNameSyntax genericName &&
                    genericName.TypeArgumentList.Arguments.Count == 1)
                {
                    var typeArg = genericName.TypeArgumentList.Arguments[0];
                    var typeInfo = _semanticModel.GetTypeInfo(typeArg);
                    displayName = typeInfo.Type?.Name ?? typeArg.ToString();
                }

                var line = node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                _pipeline.Add((displayName, methodName, line));
            }
        }

        base.VisitInvocationExpression(node);
    }

    /// <summary>
    /// R20 Issue #4 fix: filter false positives like <c>DbContextOptionsBuilder.UseNpgsql</c>
    /// and <c>ModelBuilder.UseIdentityByDefaultColumns</c> which syntactically match
    /// <c>UseXxx()</c> but are NOT ASP.NET middleware. Use the semantic model to verify
    /// that the receiver implements <c>IApplicationBuilder</c> (or is an extension method
    /// whose first parameter is <c>IApplicationBuilder</c>).
    /// </summary>
    private bool IsAspNetMiddlewareCall(InvocationExpressionSyntax invocation)
    {
        var symbolInfo = _semanticModel.GetSymbolInfo(invocation);
        if (symbolInfo.Symbol is not IMethodSymbol methodSymbol)
        {
            // Fallback when the compilation doesn't resolve: keep the old heuristic
            // for hand-written test code that lacks EF/ASP.NET references. Without
            // this, tests that reference method name alone would break.
            return true;
        }

        // Case 1: extension method — ReducedFrom holds the original signature.
        // First parameter type should be IApplicationBuilder / IEndpointRouteBuilder.
        var paramType = methodSymbol.IsExtensionMethod
            ? methodSymbol.ReducedFrom?.Parameters.FirstOrDefault()?.Type
            : methodSymbol.ContainingType;

        // Case 2: instance method — check ContainingType directly.
        paramType ??= methodSymbol.ContainingType;

        if (paramType is null) return false;

        return ImplementsOrIs(paramType, "Microsoft.AspNetCore.Builder.IApplicationBuilder") ||
               ImplementsOrIs(paramType, "Microsoft.AspNetCore.Routing.IEndpointRouteBuilder");
    }

    private static bool ImplementsOrIs(ITypeSymbol type, string fullName)
    {
        if (type.ToDisplayString() == fullName) return true;
        foreach (var iface in type.AllInterfaces)
        {
            if (iface.ToDisplayString() == fullName) return true;
        }
        return false;
    }
}
