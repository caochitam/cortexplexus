using CortexPlexus.Core.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CortexPlexus.Parsing.Extractors;

/// <summary>
/// Extracts exception flow relationships from C# code:
/// - throw new XException() → Throws edge (method → exception type)
/// - catch (XException) → Catches edge (method → exception type)
/// Uses Roslyn semantic model to resolve exception types to FQN.
/// </summary>
internal sealed class ExceptionFlowExtractor : CSharpSyntaxWalker
{
    private readonly SemanticModel _semanticModel;
    private readonly List<Relationship> _relationships = [];
    private readonly HashSet<(string, string, RelationshipType)> _seen = [];

    public IReadOnlyList<Relationship> Relationships => _relationships;

    public ExceptionFlowExtractor(SemanticModel semanticModel)
    {
        _semanticModel = semanticModel;
    }

    // --- throw new XException(...) or throw expr ---
    public override void VisitThrowStatement(ThrowStatementSyntax node)
    {
        if (node.Expression is not null)
            ExtractThrow(node.Expression, node);

        base.VisitThrowStatement(node);
    }

    public override void VisitThrowExpression(ThrowExpressionSyntax node)
    {
        ExtractThrow(node.Expression, node);
        base.VisitThrowExpression(node);
    }

    // --- catch (XException ex) { ... } ---
    public override void VisitCatchClause(CatchClauseSyntax node)
    {
        var declaration = node.Declaration;
        if (declaration?.Type is not null)
        {
            var typeInfo = _semanticModel.GetTypeInfo(declaration.Type);
            var exceptionType = typeInfo.Type;
            if (exceptionType is not null)
            {
                var callerFqn = GetEnclosingMethodFqn(node);
                if (callerFqn is not null)
                {
                    var exTypeFqn = exceptionType.ToDisplayString();
                    AddRelationship(callerFqn, exTypeFqn, RelationshipType.Catches);
                }
            }
        }

        base.VisitCatchClause(node);
    }

    private void ExtractThrow(ExpressionSyntax expression, SyntaxNode context)
    {
        ITypeSymbol? exceptionType = null;

        // throw new XException(...)
        if (expression is ObjectCreationExpressionSyntax creation)
        {
            var typeInfo = _semanticModel.GetTypeInfo(creation);
            exceptionType = typeInfo.Type;
        }
        // throw new(...) — implicit type
        else if (expression is ImplicitObjectCreationExpressionSyntax)
        {
            var typeInfo = _semanticModel.GetTypeInfo(expression);
            exceptionType = typeInfo.ConvertedType ?? typeInfo.Type;
        }
        // throw existingVar — rethrow with variable
        else
        {
            var typeInfo = _semanticModel.GetTypeInfo(expression);
            exceptionType = typeInfo.Type;
        }

        if (exceptionType is not null)
        {
            var callerFqn = GetEnclosingMethodFqn(context);
            if (callerFqn is not null)
            {
                var exTypeFqn = exceptionType.ToDisplayString();
                AddRelationship(callerFqn, exTypeFqn, RelationshipType.Throws);
            }
        }
    }

    private string? GetEnclosingMethodFqn(SyntaxNode node)
    {
        var enclosing = node.Ancestors()
            .FirstOrDefault(a => a is MethodDeclarationSyntax or ConstructorDeclarationSyntax);

        if (enclosing is null) return null;

        var symbol = _semanticModel.GetDeclaredSymbol(enclosing) as IMethodSymbol;
        return symbol is not null ? SymbolExtractor.GetFqn(symbol) : null;
    }

    private void AddRelationship(string fromFqn, string toFqn, RelationshipType type)
    {
        if (string.IsNullOrWhiteSpace(fromFqn) || string.IsNullOrWhiteSpace(toFqn))
            return;

        var key = (fromFqn, toFqn, type);
        if (_seen.Add(key))
            _relationships.Add(new Relationship(fromFqn, toFqn, type));
    }
}
