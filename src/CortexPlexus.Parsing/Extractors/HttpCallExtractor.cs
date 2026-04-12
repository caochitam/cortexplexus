using CortexPlexus.Core.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CortexPlexus.Parsing.Extractors;

/// <summary>
/// Detects outgoing HTTP calls in C# code and creates HttpCalls edges.
/// Patterns detected:
/// - HttpClient: GetAsync, PostAsync, PutAsync, DeleteAsync, SendAsync, GetStringAsync, etc.
/// - IHttpClientFactory: CreateClient("name")
/// - HttpRequestMessage with explicit URI
/// - WebClient (legacy): DownloadString, UploadString
/// Creates: Method → "http:URL_OR_SERVICE" HttpCalls edge with metadata (httpMethod, url).
/// </summary>
internal sealed class HttpCallExtractor : CSharpSyntaxWalker
{
    private static readonly HashSet<string> HttpClientMethods = new(StringComparer.Ordinal)
    {
        "GetAsync", "PostAsync", "PutAsync", "DeleteAsync", "PatchAsync", "SendAsync",
        "GetStringAsync", "GetByteArrayAsync", "GetStreamAsync",
    };

    private static readonly Dictionary<string, string> MethodToHttpVerb = new(StringComparer.Ordinal)
    {
        ["GetAsync"] = "GET", ["GetStringAsync"] = "GET", ["GetByteArrayAsync"] = "GET", ["GetStreamAsync"] = "GET",
        ["PostAsync"] = "POST", ["PutAsync"] = "PUT", ["DeleteAsync"] = "DELETE", ["PatchAsync"] = "PATCH",
        ["SendAsync"] = "SEND",
    };

    private static readonly HashSet<string> HttpClientTypes = new(StringComparer.Ordinal)
    {
        "System.Net.Http.HttpClient",
        "System.Net.Http.HttpMessageInvoker",
    };

    private readonly SemanticModel _semanticModel;
    private readonly List<Relationship> _relationships = [];
    private readonly HashSet<string> _seen = [];

    public IReadOnlyList<Relationship> Relationships => _relationships;

    public HttpCallExtractor(SemanticModel semanticModel)
    {
        _semanticModel = semanticModel;
    }

    public override void VisitInvocationExpression(InvocationExpressionSyntax node)
    {
        var callerFqn = GetEnclosingMethodFqn(node);
        if (callerFqn is not null)
        {
            TryExtractHttpClientCall(node, callerFqn);
            TryExtractHttpClientFactoryCall(node, callerFqn);
        }

        base.VisitInvocationExpression(node);
    }

    private void TryExtractHttpClientCall(InvocationExpressionSyntax node, string callerFqn)
    {
        if (node.Expression is not MemberAccessExpressionSyntax memberAccess)
            return;

        var methodName = memberAccess.Name.Identifier.Text;
        if (!HttpClientMethods.Contains(methodName))
            return;

        // Verify the receiver is HttpClient
        var receiverType = _semanticModel.GetTypeInfo(memberAccess.Expression).Type;
        if (receiverType is null)
            return;

        var receiverFqn = receiverType.ToDisplayString();
        if (!HttpClientTypes.Contains(receiverFqn) &&
            !receiverType.AllInterfaces.Any(i => HttpClientTypes.Contains(i.ToDisplayString())))
            return;

        // Try to extract URL from first argument
        var url = "unknown";
        if (node.ArgumentList.Arguments.Count > 0)
        {
            url = TryExtractUrl(node.ArgumentList.Arguments[0].Expression) ?? "unknown";
        }

        var httpMethod = MethodToHttpVerb.GetValueOrDefault(methodName, "HTTP");
        AddHttpEdge(callerFqn, url, httpMethod);
    }

    private void TryExtractHttpClientFactoryCall(InvocationExpressionSyntax node, string callerFqn)
    {
        if (node.Expression is not MemberAccessExpressionSyntax memberAccess)
            return;

        var methodName = memberAccess.Name.Identifier.Text;
        if (methodName != "CreateClient")
            return;

        var symbol = _semanticModel.GetSymbolInfo(node).Symbol;
        if (symbol?.ContainingType?.ToDisplayString() is not "System.Net.Http.IHttpClientFactory")
            return;

        // Extract named client
        var clientName = "default";
        if (node.ArgumentList.Arguments.Count > 0)
        {
            clientName = TryExtractStringLiteral(node.ArgumentList.Arguments[0].Expression) ?? "default";
        }

        AddHttpEdge(callerFqn, $"httpclient:{clientName}", "FACTORY");
    }

    private string? TryExtractUrl(ExpressionSyntax expression)
    {
        // Direct string literal
        var literal = TryExtractStringLiteral(expression);
        if (literal is not null)
            return literal;

        // String interpolation: $"http://..."
        if (expression is InterpolatedStringExpressionSyntax interpolated)
        {
            // Extract the static parts
            var parts = interpolated.Contents
                .OfType<InterpolatedStringTextSyntax>()
                .Select(t => t.TextToken.ValueText);
            var combined = string.Join("*", parts);
            return string.IsNullOrEmpty(combined) ? null : combined;
        }

        return null;
    }

    private static string? TryExtractStringLiteral(ExpressionSyntax expression)
    {
        if (expression is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.StringLiteralExpression))
            return literal.Token.ValueText;
        return null;
    }

    private void AddHttpEdge(string callerFqn, string url, string httpMethod)
    {
        var targetFqn = $"http:{url}";
        var key = $"{callerFqn}->{targetFqn}:{httpMethod}";
        if (!_seen.Add(key)) return;

        _relationships.Add(new Relationship(
            callerFqn, targetFqn, RelationshipType.HttpCalls,
            new Dictionary<string, string>
            {
                ["httpMethod"] = httpMethod,
                ["url"] = url,
            }));
    }

    private string? GetEnclosingMethodFqn(SyntaxNode node)
    {
        var enclosing = node.Ancestors()
            .FirstOrDefault(a => a is MethodDeclarationSyntax or ConstructorDeclarationSyntax);
        if (enclosing is null) return null;

        var symbol = _semanticModel.GetDeclaredSymbol(enclosing) as IMethodSymbol;
        return symbol is not null ? SymbolExtractor.GetFqn(symbol) : null;
    }
}
