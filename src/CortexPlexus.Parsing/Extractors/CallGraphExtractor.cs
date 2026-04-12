using CortexPlexus.Core.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CortexPlexus.Parsing.Extractors;

internal sealed class CallGraphExtractor : CSharpSyntaxWalker
{
    private readonly SemanticModel _semanticModel;
    private readonly List<Relationship> _relationships = [];
    private readonly HashSet<(string, string, RelationshipType)> _seen = [];

    public IReadOnlyList<Relationship> Relationships => _relationships;

    public CallGraphExtractor(SemanticModel semanticModel)
    {
        _semanticModel = semanticModel;
    }

    public override void VisitInvocationExpression(InvocationExpressionSyntax node)
    {
        var callerFqn = GetEnclosingMethodFqn(node);
        if (callerFqn is null)
        {
            base.VisitInvocationExpression(node);
            return;
        }

        var symbolInfo = _semanticModel.GetSymbolInfo(node);
        if (symbolInfo.Symbol is IMethodSymbol callee)
        {
            var calleeFqn = SymbolExtractor.GetFqn(callee);

            // If caller is test method and callee is NOT a test method → TestCovers edge
            var callerSymbol = GetEnclosingMethodSymbol(node);
            if (callerSymbol is not null && SymbolExtractor.IsTestAttribute(callerSymbol) && !SymbolExtractor.IsTestAttribute(callee))
            {
                AddRelationship(callerFqn, calleeFqn, RelationshipType.TestCovers);
            }
            else
            {
                AddRelationship(callerFqn, calleeFqn, RelationshipType.Calls);
            }
        }

        // Direct event invocation: EventName(sender, args) → Publishes edge
        // Check if the invoked expression resolves to an event
        if (node.Expression is IdentifierNameSyntax or MemberAccessExpressionSyntax)
        {
            var exprSymbol = _semanticModel.GetSymbolInfo(node.Expression);
            if (exprSymbol.Symbol is IEventSymbol eventSymbol)
            {
                var eventFqn = SymbolExtractor.GetFqn(eventSymbol);
                AddRelationship(callerFqn, eventFqn, RelationshipType.Publishes);
            }
        }

        base.VisitInvocationExpression(node);
    }

    public override void VisitObjectCreationExpression(ObjectCreationExpressionSyntax node)
    {
        var callerFqn = GetEnclosingMethodFqn(node);
        if (callerFqn is null)
        {
            base.VisitObjectCreationExpression(node);
            return;
        }

        var symbolInfo = _semanticModel.GetSymbolInfo(node);
        if (symbolInfo.Symbol is IMethodSymbol ctorSymbol && ctorSymbol.ContainingType is not null)
        {
            AddRelationship(callerFqn, SymbolExtractor.GetFqn(ctorSymbol.ContainingType), RelationshipType.Creates);
        }

        base.VisitObjectCreationExpression(node);
    }

    public override void VisitImplicitObjectCreationExpression(ImplicitObjectCreationExpressionSyntax node)
    {
        var callerFqn = GetEnclosingMethodFqn(node);
        if (callerFqn is null)
        {
            base.VisitImplicitObjectCreationExpression(node);
            return;
        }

        var symbolInfo = _semanticModel.GetSymbolInfo(node);
        if (symbolInfo.Symbol is IMethodSymbol ctorSymbol && ctorSymbol.ContainingType is not null)
        {
            AddRelationship(callerFqn, SymbolExtractor.GetFqn(ctorSymbol.ContainingType), RelationshipType.Creates);
        }

        base.VisitImplicitObjectCreationExpression(node);
    }

    public override void VisitMemberAccessExpression(MemberAccessExpressionSyntax node)
    {
        // Track property access: method uses obj.Property → References edge
        // This catches DI patterns like currentUserService.UserId
        var symbolInfo = _semanticModel.GetSymbolInfo(node);
        if (symbolInfo.Symbol is IPropertySymbol property)
        {
            var callerFqn = GetEnclosingMethodFqn(node);
            if (callerFqn is not null && property.ContainingType is not null)
            {
                // Emit References from enclosing method → containing type (not property itself)
                // This enables get_callers to find "who uses ICurrentUserService"
                var targetFqn = SymbolExtractor.GetFqn(property.ContainingType);
                AddRelationship(callerFqn, targetFqn, RelationshipType.References);
            }
        }

        base.VisitMemberAccessExpression(node);
    }

    // --- Event subscription: obj.EventName += handler → Subscribes edge ---
    // --- Event raise: EventName?.Invoke() or EventName(args) → Publishes edge ---

    public override void VisitAssignmentExpression(AssignmentExpressionSyntax node)
    {
        // event += handler → Subscribes
        // event -= handler → (ignore, we only track subscriptions)
        if (node.IsKind(SyntaxKind.AddAssignmentExpression))
        {
            var symbolInfo = _semanticModel.GetSymbolInfo(node.Left);
            if (symbolInfo.Symbol is IEventSymbol eventSymbol)
            {
                var callerFqn = GetEnclosingMethodFqn(node);
                if (callerFqn is not null)
                {
                    var eventFqn = SymbolExtractor.GetFqn(eventSymbol);
                    AddRelationship(callerFqn, eventFqn, RelationshipType.Subscribes);
                }
            }
        }

        base.VisitAssignmentExpression(node);
    }

    public override void VisitConditionalAccessExpression(ConditionalAccessExpressionSyntax node)
    {
        // EventName?.Invoke(args) → Publishes edge
        var symbolInfo = _semanticModel.GetSymbolInfo(node.Expression);
        if (symbolInfo.Symbol is IEventSymbol eventSymbol)
        {
            var callerFqn = GetEnclosingMethodFqn(node);
            if (callerFqn is not null)
            {
                var eventFqn = SymbolExtractor.GetFqn(eventSymbol);
                AddRelationship(callerFqn, eventFqn, RelationshipType.Publishes);
            }
        }

        base.VisitConditionalAccessExpression(node);
    }

    private string? GetEnclosingMethodFqn(SyntaxNode node)
    {
        var symbol = GetEnclosingMethodSymbol(node);
        return symbol is not null ? SymbolExtractor.GetFqn(symbol) : null;
    }

    private IMethodSymbol? GetEnclosingMethodSymbol(SyntaxNode node)
    {
        var enclosing = node.Ancestors()
            .FirstOrDefault(a => a is MethodDeclarationSyntax or ConstructorDeclarationSyntax);

        if (enclosing is null) return null;

        return _semanticModel.GetDeclaredSymbol(enclosing) as IMethodSymbol;
    }

    private void AddRelationship(string fromFqn, string toFqn, RelationshipType type)
    {
        if (string.IsNullOrWhiteSpace(fromFqn) || string.IsNullOrWhiteSpace(toFqn))
            return;

        var key = (fromFqn, toFqn, type);
        if (_seen.Add(key))
        {
            _relationships.Add(new Relationship(fromFqn, toFqn, type));
        }
    }
}
