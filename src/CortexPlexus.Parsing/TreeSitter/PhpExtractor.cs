using CortexPlexus.Core.Models;

namespace CortexPlexus.Parsing.TreeSitter;

/// <summary>
/// Walks a tree-sitter AST to extract symbols and relationships for PHP source files.
/// FQN format: App\Services\ClassName::methodName
/// </summary>
internal sealed class PhpExtractor
{
    private readonly string _filePath;
    private readonly string _relativePath;
    private string _namespace;

    private readonly List<CodeSymbol> _symbols = [];
    private readonly List<Relationship> _relationships = [];
    private readonly HashSet<string> _testMethodFqns = [];
    private readonly HashSet<string> _testCaseClasses = [];

    public PhpExtractor(string sourceCode, string filePath, string relativePath)
    {
        _filePath = filePath;
        _relativePath = relativePath;
        _namespace = Path.GetFileNameWithoutExtension(relativePath);
    }

    public (List<CodeSymbol> Symbols, List<Relationship> Relationships) Extract(global::TreeSitter.Node root)
    {
        // First pass: find namespace
        ScanForNamespace(root);
        WalkNode(root, containingClass: null);
        return (_symbols, _relationships);
    }

    private void ScanForNamespace(global::TreeSitter.Node node)
    {
        foreach (var child in node.Children)
        {
            if (child.Type == "namespace_definition")
            {
                var nameNode = child.GetChildForField("name");
                if (nameNode is not null)
                    _namespace = NodeText(nameNode);
                return;
            }
            // PHP files wrapped in <?php ... ?> — the program node contains a text child
            if (child.Type == "namespace_definition" || child.Children.Any())
                ScanForNamespace(child);
        }
    }

    private void WalkNode(global::TreeSitter.Node node, string? containingClass)
    {
        switch (node.Type)
        {
            case "class_declaration":
                ExtractClass(node);
                return;
            case "interface_declaration":
                ExtractInterface(node);
                return;
            case "trait_declaration":
                ExtractTrait(node);
                return;
            case "method_declaration":
                ExtractMethod(node, containingClass);
                return;
            case "function_definition":
                ExtractFunction(node);
                return;
            case "namespace_use_declaration":
                ExtractUse(node);
                break;
            case "function_call_expression":
            case "member_call_expression":
                ExtractCall(node, containingClass);
                break;
        }

        WalkChildren(node, containingClass);
    }

    private void WalkChildren(global::TreeSitter.Node node, string? containingClass)
    {
        foreach (var child in node.Children)
            WalkNode(child, containingClass);
    }

    private void ExtractClass(global::TreeSitter.Node node)
    {
        var name = GetNameText(node);
        if (name is null) return;

        var fqn = $@"{_namespace}\{name}";
        string? baseType = null;

        // Base class
        var baseClause = node.GetChildForField("base_clause");
        if (baseClause is not null)
        {
            var baseName = FindChildByType(baseClause, "name") ?? FindChildByType(baseClause, "qualified_name");
            if (baseName is not null)
            {
                baseType = NodeText(baseName);
                _relationships.Add(new Relationship(fqn, baseType, RelationshipType.Inherits));
            }
        }

        // Interfaces
        var implClause = FindChildByType(node, "class_interface_clause");
        if (implClause is not null)
        {
            foreach (var child in implClause.Children)
            {
                if (child.IsNamed && child.Type is "name" or "qualified_name")
                    _relationships.Add(new Relationship(fqn, NodeText(child), RelationshipType.Implements));
            }
        }

        var modifiers = GetVisibility(node);

        _symbols.Add(new ClassInfo
        {
            Fqn = fqn, Name = name, Kind = "class",
            FilePath = _filePath,
            StartLine = (int)node.StartPosition.Row + 1,
            EndLine = (int)node.EndPosition.Row + 1,
            Accessibility = modifiers,
            BaseTypeFqn = baseType,
            Documentation = DocCommentHelper.GetPrecedingDocComment(node),
        });

        // Track classes extending TestCase
        if (baseType is not null && baseType.Contains("TestCase"))
            _testCaseClasses.Add(fqn);

        _relationships.Add(new Relationship(_namespace, fqn, RelationshipType.Declares));

        // Walk body — look for use (traits) and methods
        var body = node.GetChildForField("body") ?? FindChildByType(node, "declaration_list");
        if (body is not null)
        {
            // Trait use inside class
            foreach (var child in body.Children)
            {
                if (child.Type == "use_declaration")
                {
                    foreach (var traitChild in child.Children)
                    {
                        if (traitChild.IsNamed && traitChild.Type is "name" or "qualified_name")
                            _relationships.Add(new Relationship(fqn, NodeText(traitChild), RelationshipType.Implements));
                    }
                }
            }
            WalkChildren(body, fqn);
        }
    }

    private void ExtractInterface(global::TreeSitter.Node node)
    {
        var name = GetNameText(node);
        if (name is null) return;

        var fqn = $@"{_namespace}\{name}";

        _symbols.Add(new InterfaceInfo
        {
            Fqn = fqn, Name = name, Kind = "interface",
            FilePath = _filePath,
            StartLine = (int)node.StartPosition.Row + 1,
            EndLine = (int)node.EndPosition.Row + 1,
            Documentation = DocCommentHelper.GetPrecedingDocComment(node),
        });

        _relationships.Add(new Relationship(_namespace, fqn, RelationshipType.Declares));

        var body = node.GetChildForField("body") ?? FindChildByType(node, "declaration_list");
        if (body is not null)
            WalkChildren(body, fqn);
    }

    private void ExtractTrait(global::TreeSitter.Node node)
    {
        var name = GetNameText(node);
        if (name is null) return;

        var fqn = $@"{_namespace}\{name}";

        _symbols.Add(new ClassInfo
        {
            Fqn = fqn, Name = name, Kind = "trait",
            FilePath = _filePath,
            StartLine = (int)node.StartPosition.Row + 1,
            EndLine = (int)node.EndPosition.Row + 1,
            Documentation = DocCommentHelper.GetPrecedingDocComment(node),
        });

        _relationships.Add(new Relationship(_namespace, fqn, RelationshipType.Declares));

        var body = node.GetChildForField("body") ?? FindChildByType(node, "declaration_list");
        if (body is not null)
            WalkChildren(body, fqn);
    }

    private void ExtractMethod(global::TreeSitter.Node node, string? containingClass)
    {
        var name = GetNameText(node);
        if (name is null) return;

        var fqn = containingClass is not null ? $"{containingClass}::{name}" : $@"{_namespace}\{name}";
        var isTest = name.StartsWith("test", StringComparison.Ordinal)
                     || (containingClass is not null && _testCaseClasses.Contains(containingClass) && name.StartsWith("test", StringComparison.Ordinal));

        _symbols.Add(new MethodInfo
        {
            Fqn = fqn, Name = name, Kind = "method",
            FilePath = _filePath,
            StartLine = (int)node.StartPosition.Row + 1,
            EndLine = (int)node.EndPosition.Row + 1,
            Signature = BuildSignature(name, node),
            Accessibility = GetVisibility(node),
            IsStatic = HasModifier(node, "static"),
            IsTestMethod = isTest,
            ContainingTypeFqn = containingClass,
            Documentation = DocCommentHelper.GetPrecedingDocComment(node),
        });

        if (isTest) _testMethodFqns.Add(fqn);

        if (containingClass is not null)
            _relationships.Add(new Relationship(containingClass, fqn, RelationshipType.HasMethod));
        else
            _relationships.Add(new Relationship(_namespace, fqn, RelationshipType.Declares));

        var body = node.GetChildForField("body");
        if (body is not null)
        {
            _relationships.AddRange(ConfigAccessDetector.DetectPhp(body, fqn));
            _relationships.AddRange(HttpCallDetector.DetectPhp(body, fqn));
            WalkChildren(body, containingClass);
        }
    }

    private void ExtractFunction(global::TreeSitter.Node node)
    {
        var name = GetNameText(node);
        if (name is null) return;

        var fqn = $@"{_namespace}\{name}";

        var isTest = name.StartsWith("test", StringComparison.Ordinal);

        _symbols.Add(new MethodInfo
        {
            Fqn = fqn, Name = name, Kind = "function",
            FilePath = _filePath,
            StartLine = (int)node.StartPosition.Row + 1,
            EndLine = (int)node.EndPosition.Row + 1,
            Signature = BuildSignature(name, node),
            IsTestMethod = isTest,
            Documentation = DocCommentHelper.GetPrecedingDocComment(node),
        });

        if (isTest) _testMethodFqns.Add(fqn);

        _relationships.Add(new Relationship(_namespace, fqn, RelationshipType.Declares));

        var body = node.GetChildForField("body");
        if (body is not null)
        {
            _relationships.AddRange(ConfigAccessDetector.DetectPhp(body, fqn));
            _relationships.AddRange(HttpCallDetector.DetectPhp(body, fqn));
            WalkChildren(body, null);
        }
    }

    private void ExtractUse(global::TreeSitter.Node node)
    {
        foreach (var child in node.Children)
        {
            if (child.IsNamed && child.Type is "namespace_use_clause" or "qualified_name" or "name")
            {
                var importPath = NodeText(child);
                _relationships.Add(new Relationship(
                    _namespace, importPath, RelationshipType.DependsOn,
                    new Dictionary<string, string> { ["importPath"] = importPath }));
            }
        }
    }

    private void ExtractCall(global::TreeSitter.Node node, string? containingClass)
    {
        var nameNode = node.GetChildForField("name") ?? node.GetChildForField("function");
        if (nameNode is null) return;

        var calleeName = NodeText(nameNode);
        var callerFqn = containingClass ?? _namespace;
        var relType = _testMethodFqns.Contains(callerFqn) ? RelationshipType.TestCovers : RelationshipType.Calls;
        _relationships.Add(new Relationship(callerFqn, calleeName, relType));
    }

    // --- Helpers ---

    private string? GetNameText(global::TreeSitter.Node node)
    {
        var nameNode = node.GetChildForField("name");
        return nameNode is not null && nameNode.IsNamed ? NodeText(nameNode) : null;
    }

    private static string NodeText(global::TreeSitter.Node node) => node.Text ?? string.Empty;

    private static global::TreeSitter.Node? FindChildByType(global::TreeSitter.Node node, string type)
    {
        foreach (var child in node.Children)
            if (child.Type == type) return child;
        return null;
    }

    private static string? GetVisibility(global::TreeSitter.Node node)
    {
        foreach (var child in node.Children)
        {
            if (child.Type == "visibility_modifier")
                return NodeText(child);
        }
        return null;
    }

    private static bool HasModifier(global::TreeSitter.Node node, string modifier)
    {
        foreach (var child in node.Children)
        {
            if (child.Type == "static_modifier" || (child.Type == modifier) ||
                (NodeText(child) == modifier))
                return true;
        }
        return false;
    }

    private static string BuildSignature(string name, global::TreeSitter.Node node)
    {
        var paramsNode = node.GetChildForField("parameters");
        return paramsNode is not null ? $"{name}{NodeText(paramsNode)}" : $"{name}()";
    }
}
