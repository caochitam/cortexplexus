using CortexPlexus.Core.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CortexPlexus.Parsing.Extractors;

/// <summary>
/// Extracts type dependency edges:
/// - DependsOn: constructor parameter types (DI injection)
/// - UsesType: field/property/method parameter/return types
/// These edges power the get_dependencies and get_data_flow MCP tools.
/// </summary>
internal sealed class TypeDependencyExtractor : CSharpSyntaxWalker
{
    private readonly SemanticModel _semanticModel;
    private readonly List<Relationship> _relationships = [];
    private readonly HashSet<(string, string, RelationshipType)> _seen = [];

    public IReadOnlyList<Relationship> Relationships => _relationships;

    public TypeDependencyExtractor(SemanticModel semanticModel)
    {
        _semanticModel = semanticModel;
    }

    public override void VisitConstructorDeclaration(ConstructorDeclarationSyntax node)
    {
        if (_semanticModel.GetDeclaredSymbol(node) is not IMethodSymbol ctor)
        {
            base.VisitConstructorDeclaration(node);
            return;
        }

        var containingFqn = ctor.ContainingType is not null
            ? SymbolExtractor.GetFqn(ctor.ContainingType)
            : null;

        if (containingFqn is null)
        {
            base.VisitConstructorDeclaration(node);
            return;
        }

        // Constructor params → DependsOn (DI dependencies)
        foreach (var param in ctor.Parameters)
        {
            var typeFqn = GetTypeFqn(param.Type);
            if (typeFqn is not null)
                AddRelationship(containingFqn, typeFqn, RelationshipType.DependsOn);
        }

        base.VisitConstructorDeclaration(node);
    }

    public override void VisitPropertyDeclaration(PropertyDeclarationSyntax node)
    {
        if (_semanticModel.GetDeclaredSymbol(node) is not IPropertySymbol prop)
        {
            base.VisitPropertyDeclaration(node);
            return;
        }

        var containingFqn = prop.ContainingType is not null
            ? SymbolExtractor.GetFqn(prop.ContainingType)
            : null;

        if (containingFqn is null)
        {
            base.VisitPropertyDeclaration(node);
            return;
        }

        var typeFqn = GetTypeFqn(prop.Type);
        if (typeFqn is not null)
            AddRelationship(containingFqn, typeFqn, RelationshipType.UsesType);

        base.VisitPropertyDeclaration(node);
    }

    public override void VisitFieldDeclaration(FieldDeclarationSyntax node)
    {
        var typeInfo = _semanticModel.GetTypeInfo(node.Declaration.Type);
        var typeFqn = typeInfo.Type is not null ? GetTypeFqn(typeInfo.Type) : null;

        if (typeFqn is not null)
        {
            foreach (var variable in node.Declaration.Variables)
            {
                var symbol = _semanticModel.GetDeclaredSymbol(variable);
                var containingFqn = symbol?.ContainingType is not null
                    ? SymbolExtractor.GetFqn(symbol.ContainingType)
                    : null;

                if (containingFqn is not null)
                    AddRelationship(containingFqn, typeFqn, RelationshipType.UsesType);
            }
        }

        base.VisitFieldDeclaration(node);
    }

    public override void VisitMethodDeclaration(MethodDeclarationSyntax node)
    {
        if (_semanticModel.GetDeclaredSymbol(node) is not IMethodSymbol method)
        {
            base.VisitMethodDeclaration(node);
            return;
        }

        var containingFqn = method.ContainingType is not null
            ? SymbolExtractor.GetFqn(method.ContainingType)
            : null;

        if (containingFqn is null)
        {
            base.VisitMethodDeclaration(node);
            return;
        }

        // Return type → UsesType
        var returnFqn = GetTypeFqn(method.ReturnType);
        if (returnFqn is not null)
            AddRelationship(containingFqn, returnFqn, RelationshipType.UsesType);

        // Parameter types → UsesType
        foreach (var param in method.Parameters)
        {
            var paramFqn = GetTypeFqn(param.Type);
            if (paramFqn is not null)
                AddRelationship(containingFqn, paramFqn, RelationshipType.UsesType);
        }

        base.VisitMethodDeclaration(node);
    }

    /// <summary>
    /// Get FQN for a type, unwrapping Task&lt;T&gt;, IEnumerable&lt;T&gt;, etc. to get the inner type.
    /// Skip primitive types (int, string, bool, etc.) and System types.
    /// </summary>
    private static string? GetTypeFqn(ITypeSymbol type)
    {
        // Unwrap Task<T>, ValueTask<T>
        if (type is INamedTypeSymbol { IsGenericType: true } named)
        {
            var fullName = named.ConstructedFrom.ToDisplayString();
            if (fullName is "System.Threading.Tasks.Task<TResult>"
                         or "System.Threading.Tasks.ValueTask<TResult>")
            {
                type = named.TypeArguments[0];
            }
        }

        // Skip void, primitives, System base types
        if (type.SpecialType != SpecialType.None)
            return null;

        // Skip common framework types that add noise
        var ns = type.ContainingNamespace?.ToDisplayString() ?? "";
        if (ns.StartsWith("System") || ns.StartsWith("Microsoft.Extensions"))
            return null;

        // Unwrap IEnumerable<T>, IReadOnlyList<T>, List<T>, etc. to get inner type
        if (type is INamedTypeSymbol { IsGenericType: true } collection)
        {
            var baseName = collection.ConstructedFrom.ToDisplayString();
            if (baseName.Contains("IEnumerable") || baseName.Contains("IReadOnlyList")
                || baseName.Contains("IList") || baseName.Contains("List")
                || baseName.Contains("IReadOnlyCollection") || baseName.Contains("ICollection"))
            {
                if (collection.TypeArguments.Length == 1)
                {
                    var innerFqn = GetTypeFqn(collection.TypeArguments[0]);
                    if (innerFqn is not null)
                        return innerFqn;
                }
            }
        }

        var fqn = SymbolExtractor.GetFqn(type);
        return string.IsNullOrWhiteSpace(fqn) ? null : fqn;
    }

    private void AddRelationship(string fromFqn, string toFqn, RelationshipType type)
    {
        if (string.IsNullOrWhiteSpace(fromFqn) || string.IsNullOrWhiteSpace(toFqn))
            return;
        if (fromFqn == toFqn) // Self-reference
            return;

        var key = (fromFqn, toFqn, type);
        if (_seen.Add(key))
            _relationships.Add(new Relationship(fromFqn, toFqn, type));
    }
}
