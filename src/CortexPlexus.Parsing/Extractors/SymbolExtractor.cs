using CortexPlexus.Core.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CortexPlexus.Parsing.Extractors;

internal sealed class SymbolExtractor : CSharpSyntaxWalker
{
    private readonly SemanticModel _semanticModel;
    private readonly List<CodeSymbol> _symbols = [];
    private readonly List<Relationship> _relationships = [];
    private int _skippedCount;

    public IReadOnlyList<CodeSymbol> Symbols => _symbols;
    public IReadOnlyList<Relationship> Relationships => _relationships;
    public int SkippedCount => _skippedCount;

    public SymbolExtractor(SemanticModel semanticModel)
    {
        _semanticModel = semanticModel;
    }

    public override void VisitClassDeclaration(ClassDeclarationSyntax node)
    {
        ExtractTypeDeclaration(node, "class");
        base.VisitClassDeclaration(node);
    }

    public override void VisitStructDeclaration(StructDeclarationSyntax node)
    {
        ExtractTypeDeclaration(node, "struct");
        base.VisitStructDeclaration(node);
    }

    public override void VisitRecordDeclaration(RecordDeclarationSyntax node)
    {
        var kind = node.ClassOrStructKeyword.IsKind(SyntaxKind.StructKeyword)
            ? "record struct"
            : "record";
        ExtractTypeDeclaration(node, kind);
        base.VisitRecordDeclaration(node);
    }

    public override void VisitEnumDeclaration(EnumDeclarationSyntax node)
    {
        if (_semanticModel.GetDeclaredSymbol(node) is not INamedTypeSymbol symbol)
            return;

        var fqn = GetFqn(symbol);
        var filePath = node.SyntaxTree.FilePath;
        var lineSpan = node.GetLocation().GetLineSpan();

        AddSymbol(new ClassInfo
        {
            Fqn = fqn,
            Name = symbol.Name,
            Kind = "enum",
            FilePath = filePath,
            StartLine = lineSpan.StartLinePosition.Line + 1,
            EndLine = lineSpan.EndLinePosition.Line + 1,
            Accessibility = symbol.DeclaredAccessibility.ToString().ToLowerInvariant(),
            Documentation = ExtractDocumentation(symbol)
        });

        EmitNamespaceDeclares(symbol, fqn);

        base.VisitEnumDeclaration(node);
    }

    public override void VisitInterfaceDeclaration(InterfaceDeclarationSyntax node)
    {
        if (_semanticModel.GetDeclaredSymbol(node) is not INamedTypeSymbol symbol)
            return;

        var fqn = GetFqn(symbol);
        var filePath = node.SyntaxTree.FilePath;
        var lineSpan = node.GetLocation().GetLineSpan();

        var memberFqns = symbol.GetMembers()
            .Where(m => m is IMethodSymbol or IPropertySymbol)
            .Select(m => GetFqn(m))
            .ToList();

        AddSymbol(new InterfaceInfo
        {
            Fqn = fqn,
            Name = symbol.Name,
            Kind = "interface",
            FilePath = filePath,
            StartLine = lineSpan.StartLinePosition.Line + 1,
            EndLine = lineSpan.EndLinePosition.Line + 1,
            Accessibility = symbol.DeclaredAccessibility.ToString().ToLowerInvariant(),
            MemberFqns = memberFqns,
            Documentation = ExtractDocumentation(symbol)
        });

        EmitNamespaceDeclares(symbol, fqn);

        // Emit INHERITS for base interfaces
        foreach (var baseInterface in symbol.Interfaces)
        {
            AddRelationship(fqn, GetFqn(baseInterface), RelationshipType.Inherits);
        }

        base.VisitInterfaceDeclaration(node);
    }

    public override void VisitMethodDeclaration(MethodDeclarationSyntax node)
    {
        if (_semanticModel.GetDeclaredSymbol(node) is not IMethodSymbol symbol)
            return;

        var fqn = GetFqn(symbol);
        var filePath = node.SyntaxTree.FilePath;
        var lineSpan = node.GetLocation().GetLineSpan();

        var parameters = symbol.Parameters
            .Select((p, i) => new ParameterInfo(p.Name, p.Type.ToDisplayString(), i))
            .ToList();

        // P2c: Code Metrics
        var body = (SyntaxNode?)node.Body ?? node.ExpressionBody;
        int? cyclomaticComplexity = null;
        int? maxNestingDepth = null;
        int? lineCount = null;

        if (body is not null)
        {
            var (cc, depth) = CodeMetricsAnalyzer.Analyze(body);
            cyclomaticComplexity = cc;
            maxNestingDepth = depth;
        }
        var startLine = lineSpan.StartLinePosition.Line + 1;
        var endLine = lineSpan.EndLinePosition.Line + 1;
        lineCount = endLine - startLine + 1;

        AddSymbol(new MethodInfo
        {
            Fqn = fqn,
            Name = symbol.Name,
            Kind = "method",
            FilePath = filePath,
            StartLine = startLine,
            EndLine = endLine,
            Signature = symbol.ToDisplayString(),
            ReturnType = symbol.ReturnType.ToDisplayString(),
            Accessibility = symbol.DeclaredAccessibility.ToString().ToLowerInvariant(),
            IsAsync = symbol.IsAsync,
            IsStatic = symbol.IsStatic,
            IsVirtual = symbol.IsVirtual,
            IsOverride = symbol.IsOverride,
            IsTestMethod = IsTestAttribute(symbol),
            ContainingTypeFqn = symbol.ContainingType is not null ? GetFqn(symbol.ContainingType) : null,
            Parameters = parameters,
            Documentation = ExtractDocumentation(symbol),
            CyclomaticComplexity = cyclomaticComplexity,
            MaxNestingDepth = maxNestingDepth,
            LineCount = lineCount,
        });

        // HAS_METHOD: type → method
        if (symbol.ContainingType is not null)
        {
            AddRelationship(GetFqn(symbol.ContainingType), fqn, RelationshipType.HasMethod);
        }

        // OVERRIDES
        if (symbol.IsOverride && symbol.OverriddenMethod is not null)
        {
            AddRelationship(fqn, GetFqn(symbol.OverriddenMethod), RelationshipType.Overrides);
        }

        base.VisitMethodDeclaration(node);
    }

    public override void VisitConstructorDeclaration(ConstructorDeclarationSyntax node)
    {
        if (_semanticModel.GetDeclaredSymbol(node) is not IMethodSymbol symbol)
            return;

        var fqn = GetFqn(symbol);
        var filePath = node.SyntaxTree.FilePath;
        var lineSpan = node.GetLocation().GetLineSpan();

        var parameters = symbol.Parameters
            .Select((p, i) => new ParameterInfo(p.Name, p.Type.ToDisplayString(), i))
            .ToList();

        AddSymbol(new ConstructorInfo
        {
            Fqn = fqn,
            Name = symbol.ContainingType?.Name + ".ctor",
            Kind = "constructor",
            FilePath = filePath,
            StartLine = lineSpan.StartLinePosition.Line + 1,
            EndLine = lineSpan.EndLinePosition.Line + 1,
            Signature = symbol.ToDisplayString(),
            Accessibility = symbol.DeclaredAccessibility.ToString().ToLowerInvariant(),
            ContainingTypeFqn = symbol.ContainingType is not null ? GetFqn(symbol.ContainingType) : null,
            Parameters = parameters,
            Documentation = ExtractDocumentation(symbol)
        });

        // HAS_CONSTRUCTOR: type → constructor
        if (symbol.ContainingType is not null)
        {
            AddRelationship(GetFqn(symbol.ContainingType), fqn, RelationshipType.HasConstructor);
        }

        base.VisitConstructorDeclaration(node);
    }

    public override void VisitPropertyDeclaration(PropertyDeclarationSyntax node)
    {
        if (_semanticModel.GetDeclaredSymbol(node) is not IPropertySymbol symbol)
            return;

        var fqn = GetFqn(symbol);
        var filePath = node.SyntaxTree.FilePath;
        var lineSpan = node.GetLocation().GetLineSpan();

        AddSymbol(new PropertyInfo
        {
            Fqn = fqn,
            Name = symbol.Name,
            Kind = "property",
            FilePath = filePath,
            StartLine = lineSpan.StartLinePosition.Line + 1,
            EndLine = lineSpan.EndLinePosition.Line + 1,
            Type = symbol.Type.ToDisplayString(),
            HasGetter = symbol.GetMethod is not null,
            HasSetter = symbol.SetMethod is not null,
            ContainingTypeFqn = symbol.ContainingType is not null ? GetFqn(symbol.ContainingType) : null,
            Documentation = ExtractDocumentation(symbol)
        });

        // HAS_PROPERTY: type → property
        if (symbol.ContainingType is not null)
        {
            AddRelationship(GetFqn(symbol.ContainingType), fqn, RelationshipType.HasProperty);
        }

        base.VisitPropertyDeclaration(node);
    }

    public override void VisitFieldDeclaration(FieldDeclarationSyntax node)
    {
        foreach (var variable in node.Declaration.Variables)
        {
            if (_semanticModel.GetDeclaredSymbol(variable) is not IFieldSymbol symbol)
                continue;

            // Skip compiler-generated backing fields (e.g., auto-properties, events)
            if (symbol.IsImplicitlyDeclared || symbol.AssociatedSymbol is not null)
                continue;

            var fqn = GetFqn(symbol);
            var filePath = node.SyntaxTree.FilePath;
            var lineSpan = variable.GetLocation().GetLineSpan();

            string? constantValue = null;
            if (symbol.HasConstantValue && symbol.ConstantValue is not null)
            {
                constantValue = symbol.ConstantValue.ToString();
                if (constantValue?.Length > 100)
                    constantValue = constantValue[..100] + "...";
            }

            AddSymbol(new FieldInfo
            {
                Fqn = fqn,
                Name = symbol.Name,
                Kind = symbol.IsConst ? "const" : "field",
                FilePath = filePath,
                StartLine = lineSpan.StartLinePosition.Line + 1,
                EndLine = lineSpan.EndLinePosition.Line + 1,
                Type = symbol.Type.ToDisplayString(),
                Accessibility = symbol.DeclaredAccessibility.ToString().ToLowerInvariant(),
                IsStatic = symbol.IsStatic,
                IsReadOnly = symbol.IsReadOnly,
                IsConst = symbol.IsConst,
                ConstantValue = constantValue,
                ContainingTypeFqn = symbol.ContainingType is not null ? GetFqn(symbol.ContainingType) : null,
                Documentation = ExtractDocumentation(symbol)
            });

            if (symbol.ContainingType is not null)
            {
                AddRelationship(GetFqn(symbol.ContainingType), fqn, RelationshipType.HasField);
            }
        }

        base.VisitFieldDeclaration(node);
    }

    public override void VisitEventFieldDeclaration(EventFieldDeclarationSyntax node)
    {
        foreach (var variable in node.Declaration.Variables)
        {
            if (_semanticModel.GetDeclaredSymbol(variable) is not IEventSymbol symbol)
                continue;

            var fqn = GetFqn(symbol);
            var filePath = node.SyntaxTree.FilePath;
            var lineSpan = variable.GetLocation().GetLineSpan();

            AddSymbol(new EventInfo
            {
                Fqn = fqn,
                Name = symbol.Name,
                Kind = "event",
                FilePath = filePath,
                StartLine = lineSpan.StartLinePosition.Line + 1,
                EndLine = lineSpan.EndLinePosition.Line + 1,
                Type = symbol.Type.ToDisplayString(),
                Accessibility = symbol.DeclaredAccessibility.ToString().ToLowerInvariant(),
                IsStatic = symbol.IsStatic,
                ContainingTypeFqn = symbol.ContainingType is not null ? GetFqn(symbol.ContainingType) : null,
                Documentation = ExtractDocumentation(symbol)
            });

            if (symbol.ContainingType is not null)
            {
                AddRelationship(GetFqn(symbol.ContainingType), fqn, RelationshipType.HasEvent);
            }
        }

        base.VisitEventFieldDeclaration(node);
    }

    private void ExtractTypeDeclaration(TypeDeclarationSyntax node, string kind)
    {
        if (_semanticModel.GetDeclaredSymbol(node) is not INamedTypeSymbol symbol)
            return;

        var fqn = GetFqn(symbol);
        var filePath = node.SyntaxTree.FilePath;
        var lineSpan = node.GetLocation().GetLineSpan();

        var interfaceFqns = symbol.Interfaces
            .Select(i => GetFqn(i))
            .ToList();

        AddSymbol(new ClassInfo
        {
            Fqn = fqn,
            Name = symbol.Name,
            Kind = kind,
            FilePath = filePath,
            StartLine = lineSpan.StartLinePosition.Line + 1,
            EndLine = lineSpan.EndLinePosition.Line + 1,
            Accessibility = symbol.DeclaredAccessibility.ToString().ToLowerInvariant(),
            IsAbstract = symbol.IsAbstract,
            IsStatic = symbol.IsStatic,
            IsSealed = symbol.IsSealed,
            IsPartial = node.Modifiers.Any(SyntaxKind.PartialKeyword),
            BaseTypeFqn = symbol.BaseType is not null && symbol.BaseType.SpecialType != SpecialType.System_Object
                ? GetFqn(symbol.BaseType)
                : null,
            InterfaceFqns = interfaceFqns,
            Documentation = ExtractDocumentation(symbol)
        });

        EmitNamespaceDeclares(symbol, fqn);

        // INHERITS: type → base type
        if (symbol.BaseType is not null && symbol.BaseType.SpecialType != SpecialType.System_Object)
        {
            AddRelationship(fqn, GetFqn(symbol.BaseType), RelationshipType.Inherits);
        }

        // IMPLEMENTS: type → interface
        foreach (var iface in symbol.Interfaces)
        {
            AddRelationship(fqn, GetFqn(iface), RelationshipType.Implements);
        }
    }

    private void EmitNamespaceDeclares(ISymbol symbol, string typeFqn)
    {
        var ns = symbol.ContainingNamespace;
        if (ns is null || ns.IsGlobalNamespace)
            return;

        var nsFqn = ns.ToDisplayString();

        // Add namespace symbol if not already present
        if (!string.IsNullOrWhiteSpace(nsFqn) && !_symbols.Any(s => s.Fqn == nsFqn && s.Kind == "namespace"))
        {
            AddSymbol(new NamespaceInfo
            {
                Fqn = nsFqn,
                Name = ns.Name,
                Kind = "namespace"
            });
        }

        AddRelationship(nsFqn, typeFqn, RelationshipType.Declares);
    }

    // Custom format that qualifies members with their containing type (FullyQualifiedFormat only qualifies types)
    private static readonly SymbolDisplayFormat MemberQualifiedFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Included,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        memberOptions: SymbolDisplayMemberOptions.IncludeContainingType,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers);

    internal static string GetFqn(ISymbol symbol)
    {
        var format = symbol is IMethodSymbol or IPropertySymbol or IFieldSymbol or IEventSymbol
            ? MemberQualifiedFormat
            : SymbolDisplayFormat.FullyQualifiedFormat;

        return symbol
            .ToDisplayString(format)
            .Replace("global::", string.Empty);
    }

    private void AddSymbol(CodeSymbol symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol.Fqn))
        {
            _skippedCount++;
            return;
        }
        _symbols.Add(symbol);
    }

    private void AddRelationship(string fromFqn, string toFqn, RelationshipType type)
    {
        if (string.IsNullOrWhiteSpace(fromFqn) || string.IsNullOrWhiteSpace(toFqn))
            return;
        _relationships.Add(new Relationship(fromFqn, toFqn, type));
    }

    private static readonly HashSet<string> TestAttributeNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Fact", "FactAttribute",
        "Theory", "TheoryAttribute",
        "Test", "TestAttribute",
        "TestMethod", "TestMethodAttribute",
        "TestCase", "TestCaseAttribute",
    };

    internal static bool IsTestAttribute(IMethodSymbol symbol)
    {
        return symbol.GetAttributes()
            .Any(a => a.AttributeClass?.Name is not null && TestAttributeNames.Contains(a.AttributeClass.Name));
    }

    /// <summary>
    /// Extracts XML documentation comment from a Roslyn ISymbol.
    /// Returns plain text summary, or null if no documentation found.
    /// </summary>
    internal static string? ExtractDocumentation(ISymbol symbol)
    {
        var xml = symbol.GetDocumentationCommentXml();
        if (string.IsNullOrWhiteSpace(xml))
            return null;

        // Extract <summary> content
        try
        {
            var summaryStart = xml.IndexOf("<summary>", StringComparison.Ordinal);
            var summaryEnd = xml.IndexOf("</summary>", StringComparison.Ordinal);
            if (summaryStart >= 0 && summaryEnd > summaryStart)
            {
                var content = xml[(summaryStart + 9)..summaryEnd];
                // Strip XML tags and normalize whitespace
                content = System.Text.RegularExpressions.Regex.Replace(content, "<[^>]+>", "");
                content = System.Text.RegularExpressions.Regex.Replace(content, @"\s+", " ").Trim();
                return string.IsNullOrWhiteSpace(content) ? null : content;
            }
        }
        catch
        {
            // Ignore XML parse errors
        }

        return null;
    }
}
