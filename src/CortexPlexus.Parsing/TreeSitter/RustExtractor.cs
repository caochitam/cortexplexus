using CortexPlexus.Core.Models;

namespace CortexPlexus.Parsing.TreeSitter;

/// <summary>
/// Walks a tree-sitter AST to extract symbols and relationships for Rust source files.
/// FQN format: crate::module::StructName::method_name
/// </summary>
internal sealed class RustExtractor
{
    private readonly string _filePath;
    private readonly string _modulePath;

    private readonly List<CodeSymbol> _symbols = [];
    private readonly List<Relationship> _relationships = [];
    private readonly HashSet<string> _testMethodFqns = [];

    public RustExtractor(string sourceCode, string filePath, string relativePath)
    {
        _filePath = filePath;

        // Convert relative path to Rust module path:
        // "src/services/auth.rs" → "crate::services::auth"
        // "src/services/mod.rs" → "crate::services"
        // "src/main.rs" → "crate"
        // "src/lib.rs" → "crate"
        _modulePath = BuildModulePath(relativePath);
    }

    public (List<CodeSymbol> Symbols, List<Relationship> Relationships) Extract(global::TreeSitter.Node root)
    {
        WalkNode(root, containingImpl: null);
        return (_symbols, _relationships);
    }

    private void WalkNode(global::TreeSitter.Node node, string? containingImpl)
    {
        switch (node.Type)
        {
            case "struct_item":
                ExtractStruct(node);
                return;
            case "enum_item":
                ExtractEnum(node);
                return;
            case "trait_item":
                ExtractTrait(node);
                return;
            case "function_item":
                ExtractFunction(node, containingImpl);
                return;
            case "impl_item":
                ExtractImpl(node);
                return;
            case "use_declaration":
                ExtractUse(node);
                break;
            case "call_expression":
                ExtractCall(node, containingImpl);
                break;
        }

        foreach (var child in node.Children)
            WalkNode(child, containingImpl);
    }

    private void ExtractStruct(global::TreeSitter.Node node)
    {
        var name = GetNameText(node);
        if (name is null) return;

        var fqn = $"{_modulePath}::{name}";
        var isPublic = HasVisibility(node, "pub");

        _symbols.Add(new ClassInfo
        {
            Fqn = fqn, Name = name, Kind = "struct",
            FilePath = _filePath,
            StartLine = (int)node.StartPosition.Row + 1,
            EndLine = (int)node.EndPosition.Row + 1,
            Accessibility = isPublic ? "pub" : null,
            Documentation = DocCommentHelper.GetRustDocComment(node),
        });

        _relationships.Add(new Relationship(_modulePath, fqn, RelationshipType.Declares));

        // Extract struct fields
        var fieldList = FindChildByType(node, "field_declaration_list");
        if (fieldList is not null)
        {
            foreach (var field in fieldList.Children)
            {
                if (field.Type != "field_declaration") continue;
                var fieldName = GetNameText(field);
                if (fieldName is null) continue;

                var fieldFqn = $"{fqn}::{fieldName}";
                var typeNode = field.GetChildForField("type");

                _symbols.Add(new PropertyInfo
                {
                    Fqn = fieldFqn, Name = fieldName, Kind = "property",
                    FilePath = _filePath,
                    StartLine = (int)field.StartPosition.Row + 1,
                    EndLine = (int)field.EndPosition.Row + 1,
                    Type = typeNode is not null ? NodeText(typeNode) : "unknown",
                    ContainingTypeFqn = fqn,
                });
                _relationships.Add(new Relationship(fqn, fieldFqn, RelationshipType.HasProperty));
            }
        }
    }

    private void ExtractEnum(global::TreeSitter.Node node)
    {
        var name = GetNameText(node);
        if (name is null) return;

        var fqn = $"{_modulePath}::{name}";

        _symbols.Add(new ClassInfo
        {
            Fqn = fqn, Name = name, Kind = "enum",
            FilePath = _filePath,
            StartLine = (int)node.StartPosition.Row + 1,
            EndLine = (int)node.EndPosition.Row + 1,
            Accessibility = HasVisibility(node, "pub") ? "pub" : null,
            Documentation = DocCommentHelper.GetRustDocComment(node),
        });

        _relationships.Add(new Relationship(_modulePath, fqn, RelationshipType.Declares));
    }

    private void ExtractTrait(global::TreeSitter.Node node)
    {
        var name = GetNameText(node);
        if (name is null) return;

        var fqn = $"{_modulePath}::{name}";

        _symbols.Add(new InterfaceInfo
        {
            Fqn = fqn, Name = name, Kind = "interface",
            FilePath = _filePath,
            StartLine = (int)node.StartPosition.Row + 1,
            EndLine = (int)node.EndPosition.Row + 1,
            Accessibility = HasVisibility(node, "pub") ? "pub" : null,
            Documentation = DocCommentHelper.GetRustDocComment(node),
        });

        _relationships.Add(new Relationship(_modulePath, fqn, RelationshipType.Declares));

        // Walk trait body for method signatures
        var body = FindChildByType(node, "declaration_list");
        if (body is not null)
        {
            foreach (var child in body.Children)
            {
                if (child.Type == "function_signature_item")
                {
                    var methodName = GetNameText(child);
                    if (methodName is not null)
                    {
                        var methodFqn = $"{fqn}::{methodName}";
                        _symbols.Add(new MethodInfo
                        {
                            Fqn = methodFqn, Name = methodName, Kind = "method",
                            FilePath = _filePath,
                            StartLine = (int)child.StartPosition.Row + 1,
                            EndLine = (int)child.EndPosition.Row + 1,
                            Signature = $"fn {methodName}(..)",
                            ContainingTypeFqn = fqn,
                            Documentation = DocCommentHelper.GetRustDocComment(child),
                        });
                        _relationships.Add(new Relationship(fqn, methodFqn, RelationshipType.HasMethod));
                    }
                }
            }
        }
    }

    private void ExtractImpl(global::TreeSitter.Node node)
    {
        // impl StructName { ... } or impl TraitName for StructName { ... }
        var typeNode = node.GetChildForField("type");
        if (typeNode is null) return;

        var typeName = ExtractTypeName(typeNode);
        var implFqn = $"{_modulePath}::{typeName}";

        // Check if trait impl: impl Trait for Type
        var traitNode = node.GetChildForField("trait");
        if (traitNode is not null)
        {
            var traitName = ExtractTypeName(traitNode);
            _relationships.Add(new Relationship(implFqn, traitName, RelationshipType.Implements));
        }

        // Walk body — extract methods belonging to this type
        var body = FindChildByType(node, "declaration_list");
        if (body is not null)
        {
            foreach (var child in body.Children)
            {
                if (child.Type == "function_item")
                    ExtractFunction(child, implFqn);
                else
                    WalkNode(child, implFqn);
            }
        }
    }

    private static bool HasTestAttribute(global::TreeSitter.Node node)
    {
        // Check for #[test] attribute on function_item
        // In tree-sitter-rust, attributes appear as siblings before the function
        var parent = node.Parent;
        if (parent is not null)
        {
            foreach (var sibling in parent.Children)
            {
                if (sibling == node) break;
                if (sibling.Type == "attribute_item" && NodeText(sibling).Contains("test"))
                    return true;
            }
        }
        // Also check direct children (some tree-sitter versions nest attributes)
        foreach (var child in node.Children)
        {
            if (child.Type == "attribute_item" && NodeText(child).Contains("test"))
                return true;
        }
        return false;
    }

    private static bool IsInsideTestModule(global::TreeSitter.Node node)
    {
        var current = node.Parent;
        while (current is not null)
        {
            if (current.Type == "mod_item")
            {
                var nameNode = current.GetChildForField("name");
                if (nameNode is not null && NodeText(nameNode) == "tests")
                    return true;
            }
            current = current.Parent;
        }
        return false;
    }

    private void ExtractFunction(global::TreeSitter.Node node, string? containingImpl)
    {
        var name = GetNameText(node);
        if (name is null) return;

        var isMethod = containingImpl is not null;
        var fqn = isMethod ? $"{containingImpl}::{name}" : $"{_modulePath}::{name}";
        var isPublic = HasVisibility(node, "pub");
        var isAsync = HasChildWithType(node, "async");
        var isTest = HasTestAttribute(node) || IsInsideTestModule(node);

        _symbols.Add(new MethodInfo
        {
            Fqn = fqn, Name = name,
            Kind = isMethod ? "method" : "function",
            FilePath = _filePath,
            StartLine = (int)node.StartPosition.Row + 1,
            EndLine = (int)node.EndPosition.Row + 1,
            Signature = BuildSignature(name, node),
            Accessibility = isPublic ? "pub" : null,
            IsAsync = isAsync,
            IsTestMethod = isTest,
            ContainingTypeFqn = containingImpl,
            Documentation = DocCommentHelper.GetRustDocComment(node),
        });

        if (isTest) _testMethodFqns.Add(fqn);

        if (isMethod)
            _relationships.Add(new Relationship(containingImpl!, fqn, RelationshipType.HasMethod));
        else
            _relationships.Add(new Relationship(_modulePath, fqn, RelationshipType.Declares));

        // Walk body for calls and config access
        var body = node.GetChildForField("body");
        if (body is not null)
        {
            _relationships.AddRange(ConfigAccessDetector.DetectRust(body, fqn));
            _relationships.AddRange(HttpCallDetector.DetectRust(body, fqn));
            WalkCallsInBody(body, fqn);
        }
    }

    private void ExtractUse(global::TreeSitter.Node node)
    {
        // use std::io; or use std::{io, fmt};
        var argumentNode = node.GetChildForField("argument");
        if (argumentNode is null)
        {
            // Fallback: get text of all named children
            foreach (var child in node.Children)
            {
                if (child.IsNamed && child.Type is "use_tree" or "scoped_identifier" or "identifier")
                {
                    var path = NodeText(child);
                    _relationships.Add(new Relationship(
                        _modulePath, path, RelationshipType.DependsOn,
                        new Dictionary<string, string> { ["importPath"] = path }));
                }
            }
            return;
        }

        var importPath = NodeText(argumentNode);
        _relationships.Add(new Relationship(
            _modulePath, importPath, RelationshipType.DependsOn,
            new Dictionary<string, string> { ["importPath"] = importPath }));
    }

    private void ExtractCall(global::TreeSitter.Node node, string? containingImpl)
    {
        var functionNode = node.GetChildForField("function");
        if (functionNode is null) return;

        var calleeName = NodeText(functionNode);
        var callerFqn = containingImpl ?? _modulePath;
        var relType = _testMethodFqns.Contains(callerFqn) ? RelationshipType.TestCovers : RelationshipType.Calls;
        _relationships.Add(new Relationship(callerFqn, calleeName, relType));
    }

    private void WalkCallsInBody(global::TreeSitter.Node node, string callerFqn)
    {
        var relType = _testMethodFqns.Contains(callerFqn) ? RelationshipType.TestCovers : RelationshipType.Calls;
        foreach (var child in node.Children)
        {
            if (child.Type == "call_expression")
            {
                var functionNode = child.GetChildForField("function");
                if (functionNode is not null)
                    _relationships.Add(new Relationship(callerFqn, NodeText(functionNode), relType));
            }
            if (child.Children.Any())
                WalkCallsInBody(child, callerFqn);
        }
    }

    // --- Helpers ---

    private static string BuildModulePath(string relativePath)
    {
        var path = relativePath.Replace('\\', '/');

        // Strip src/ prefix
        if (path.StartsWith("src/"))
            path = path[4..];

        // Strip .rs extension
        if (path.EndsWith(".rs"))
            path = path[..^3];

        // mod.rs / main.rs / lib.rs → parent module
        if (path.EndsWith("/mod") || path is "mod" or "main" or "lib")
        {
            var lastSlash = path.LastIndexOf('/');
            path = lastSlash > 0 ? path[..lastSlash] : "";
        }

        // Convert / to :: and prefix with crate
        var modulePath = path.Replace("/", "::");
        return string.IsNullOrEmpty(modulePath) ? "crate" : $"crate::{modulePath}";
    }

    private static string ExtractTypeName(global::TreeSitter.Node node)
    {
        // Handle generic types: Type<T> → Type
        if (node.Type == "generic_type")
        {
            var typeId = FindChildByType(node, "type_identifier") ?? FindChildByType(node, "scoped_type_identifier");
            return typeId is not null ? NodeText(typeId) : NodeText(node);
        }
        if (node.Type == "scoped_type_identifier")
        {
            // path::Type → just the last segment for FQN matching
            var text = NodeText(node);
            var lastSep = text.LastIndexOf("::");
            return lastSep >= 0 ? text[(lastSep + 2)..] : text;
        }
        return NodeText(node);
    }

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

    private static bool HasChildWithType(global::TreeSitter.Node node, string type)
    {
        foreach (var child in node.Children)
            if (child.Type == type) return true;
        return false;
    }

    private static bool HasVisibility(global::TreeSitter.Node node, string keyword)
    {
        foreach (var child in node.Children)
        {
            if (child.Type == "visibility_modifier" && NodeText(child).StartsWith(keyword, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private static string BuildSignature(string name, global::TreeSitter.Node node)
    {
        var paramsNode = node.GetChildForField("parameters");
        var returnType = node.GetChildForField("return_type");
        var paramText = paramsNode is not null ? NodeText(paramsNode) : "()";
        var retText = returnType is not null ? $" -> {NodeText(returnType)}" : "";
        return $"fn {name}{paramText}{retText}";
    }
}
