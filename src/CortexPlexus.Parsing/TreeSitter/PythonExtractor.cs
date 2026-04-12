using CortexPlexus.Core.Models;

namespace CortexPlexus.Parsing.TreeSitter;

/// <summary>
/// Walks a tree-sitter AST to extract symbols and relationships for Python source files.
/// </summary>
internal sealed class PythonExtractor
{
    private readonly string _filePath;
    private readonly string _modulePath;

    private readonly List<CodeSymbol> _symbols = [];
    private readonly List<Relationship> _relationships = [];
    private readonly HashSet<string> _testMethodFqns = [];
    private readonly bool _isTestFile;

    public PythonExtractor(string sourceCode, string filePath, string relativePath)
    {
        _filePath = filePath;

        // Convert relative path to Python module path: "src/services/auth.py" -> "src.services.auth"
        _modulePath = relativePath
            .Replace('\\', '/')
            .Replace('/', '.')
            .TrimEnd('.');

        if (_modulePath.EndsWith(".py"))
            _modulePath = _modulePath[..^3];

        var normalizedPath = filePath.Replace('\\', '/');
        _isTestFile = normalizedPath.Contains("test_") || normalizedPath.Contains("/tests/");
    }

    /// <summary>
    /// Extracts all symbols and relationships from the given AST root node.
    /// </summary>
    public (List<CodeSymbol> Symbols, List<Relationship> Relationships) Extract(global::TreeSitter.Node root)
    {
        WalkNode(root, containingClass: null);
        return (_symbols, _relationships);
    }

    private void WalkNode(global::TreeSitter.Node node, string? containingClass)
    {
        switch (node.Type)
        {
            case "class_definition":
                ExtractClass(node);
                return;

            case "function_definition":
                ExtractFunction(node, containingClass);
                return;

            case "decorated_definition":
                ExtractDecorated(node, containingClass);
                return;

            case "import_statement":
                ExtractImport(node);
                break;

            case "import_from_statement":
                ExtractImportFrom(node);
                break;

            case "call":
                ExtractCall(node, containingClass);
                break;
        }

        WalkChildren(node, containingClass);
    }

    private void WalkChildren(global::TreeSitter.Node node, string? containingClass)
    {
        foreach (var child in node.Children)
        {
            WalkNode(child, containingClass);
        }
    }

    private void ExtractClass(global::TreeSitter.Node node)
    {
        var name = GetNameText(node);
        if (name is null) return;

        var fqn = $"{_modulePath}.{name}";
        var body = node.GetChildForField("body");

        _symbols.Add(new ClassInfo
        {
            Fqn = fqn,
            Name = name,
            Kind = "class",
            FilePath = _filePath,
            StartLine = (int)node.StartPosition.Row + 1,
            EndLine = (int)node.EndPosition.Row + 1,
            Documentation = body is not null ? DocCommentHelper.GetPythonDocstring(body) : null,
        });

        _relationships.Add(new Relationship(_modulePath, fqn, RelationshipType.Declares));
        ExtractBaseClasses(node, fqn);

        if (body is not null)
        {
            _relationships.AddRange(ConfigAccessDetector.DetectPython(body, fqn));
            _relationships.AddRange(HttpCallDetector.DetectPython(body, fqn));
            _relationships.AddRange(EventPatternDetector.DetectPython(body, fqn));
            WalkChildren(body, containingClass: fqn);
        }
    }

    private void ExtractBaseClasses(global::TreeSitter.Node node, string classFqn)
    {
        var superclasses = node.GetChildForField("superclasses");
        if (superclasses is null) return;

        foreach (var child in superclasses.Children)
        {
            if (child.IsNamed)
            {
                _relationships.Add(new Relationship(classFqn, NodeText(child), RelationshipType.Inherits));
            }
        }
    }

    private void ExtractFunction(global::TreeSitter.Node node, string? containingClass)
    {
        var name = GetNameText(node);
        if (name is null) return;

        var isMethod = containingClass is not null;
        var fqn = containingClass is not null
            ? $"{containingClass}.{name}"
            : $"{_modulePath}.{name}";

        var isAsync = HasChildWithType(node, "async");
        var body = node.GetChildForField("body");
        var isTest = name.StartsWith("test_") || _isTestFile && name.StartsWith("test_");

        _symbols.Add(new MethodInfo
        {
            Fqn = fqn,
            Name = name,
            Kind = isMethod ? "method" : "function",
            FilePath = _filePath,
            StartLine = (int)node.StartPosition.Row + 1,
            EndLine = (int)node.EndPosition.Row + 1,
            Signature = BuildSignature(name, node),
            IsAsync = isAsync,
            IsTestMethod = isTest,
            ContainingTypeFqn = containingClass,
            Documentation = body is not null ? DocCommentHelper.GetPythonDocstring(body) : null,
        });

        if (isTest) _testMethodFqns.Add(fqn);

        if (containingClass is not null)
            _relationships.Add(new Relationship(containingClass, fqn, RelationshipType.HasMethod));
        else
            _relationships.Add(new Relationship(_modulePath, fqn, RelationshipType.Declares));

        if (body is not null)
        {
            _relationships.AddRange(ConfigAccessDetector.DetectPython(body, fqn));
            _relationships.AddRange(HttpCallDetector.DetectPython(body, fqn));
            _relationships.AddRange(EventPatternDetector.DetectPython(body, fqn));
            WalkChildren(body, containingClass);
        }
    }

    private void ExtractDecorated(global::TreeSitter.Node node, string? containingClass)
    {
        var definition = node.GetChildForField("definition");
        if (definition is not null)
        {
            WalkNode(definition, containingClass);
        }
        else
        {
            foreach (var child in node.Children)
            {
                if (child.Type is "function_definition" or "class_definition")
                {
                    WalkNode(child, containingClass);
                    return;
                }
            }
        }
    }

    private void ExtractImport(global::TreeSitter.Node node)
    {
        foreach (var child in node.Children)
        {
            if (child.Type == "dotted_name")
            {
                var moduleName = NodeText(child);
                _relationships.Add(new Relationship(
                    _modulePath,
                    moduleName,
                    RelationshipType.DependsOn,
                    new Dictionary<string, string> { ["importPath"] = moduleName }));
            }
        }
    }

    private void ExtractImportFrom(global::TreeSitter.Node node)
    {
        var moduleNode = node.GetChildForField("module_name");
        if (moduleNode is null) return;

        var moduleName = NodeText(moduleNode);
        _relationships.Add(new Relationship(
            _modulePath,
            moduleName,
            RelationshipType.DependsOn,
            new Dictionary<string, string> { ["importPath"] = moduleName }));
    }

    private void ExtractCall(global::TreeSitter.Node node, string? containingClass)
    {
        var functionNode = node.GetChildForField("function");
        if (functionNode is null) return;

        var calleeName = functionNode.Type switch
        {
            "identifier" or "attribute" => NodeText(functionNode),
            _ => null,
        };

        if (calleeName is null) return;

        var callerFqn = containingClass ?? _modulePath;
        var relType = _testMethodFqns.Contains(callerFqn) ? RelationshipType.TestCovers : RelationshipType.Calls;
        _relationships.Add(new Relationship(callerFqn, calleeName, relType));
    }

    // --- Helpers ---

    private string? GetNameText(global::TreeSitter.Node node)
    {
        var nameNode = node.GetChildForField("name");
        return nameNode is not null && nameNode.IsNamed ? NodeText(nameNode) : null;
    }

    private static string NodeText(global::TreeSitter.Node node)
    {
        return node.Text ?? string.Empty;
    }

    private static bool HasChildWithType(global::TreeSitter.Node node, string type)
    {
        foreach (var child in node.Children)
        {
            if (child.Type == type) return true;
        }
        return false;
    }

    private string BuildSignature(string name, global::TreeSitter.Node node)
    {
        var paramsNode = node.GetChildForField("parameters");
        if (paramsNode is not null)
        {
            return $"{name}{NodeText(paramsNode)}";
        }
        return $"{name}()";
    }
}
