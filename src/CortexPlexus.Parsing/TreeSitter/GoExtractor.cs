using CortexPlexus.Core.Models;

namespace CortexPlexus.Parsing.TreeSitter;

/// <summary>
/// Walks a tree-sitter AST to extract symbols and relationships for Go source files.
/// FQN format: package.StructName.MethodName
/// </summary>
internal sealed class GoExtractor
{
    private readonly string _filePath;
    private readonly string _relativePath;
    private string _packageName;

    private readonly List<CodeSymbol> _symbols = [];
    private readonly List<Relationship> _relationships = [];
    private readonly HashSet<string> _testMethodFqns = [];
    private readonly bool _isTestFile;

    public GoExtractor(string sourceCode, string filePath, string relativePath)
    {
        _filePath = filePath;
        _relativePath = relativePath;
        _packageName = Path.GetFileNameWithoutExtension(relativePath);
        _isTestFile = filePath.EndsWith("_test.go", StringComparison.OrdinalIgnoreCase);
    }

    public (List<CodeSymbol> Symbols, List<Relationship> Relationships) Extract(global::TreeSitter.Node root)
    {
        // First pass: find package clause
        foreach (var child in root.Children)
        {
            if (child.Type == "package_clause")
            {
                var nameNode = FindChildByType(child, "package_identifier");
                if (nameNode is not null)
                    _packageName = NodeText(nameNode);
                break;
            }
        }

        WalkNode(root);
        return (_symbols, _relationships);
    }

    private void WalkNode(global::TreeSitter.Node node)
    {
        switch (node.Type)
        {
            case "function_declaration":
                ExtractFunction(node);
                return;
            case "method_declaration":
                ExtractMethod(node);
                return;
            case "type_declaration":
                ExtractTypeDeclaration(node);
                return;
            case "import_declaration":
                ExtractImport(node);
                break;
            case "call_expression":
                ExtractCall(node);
                break;
        }

        foreach (var child in node.Children)
            WalkNode(child);
    }

    private bool IsGoTestFunction(string name)
    {
        // Go convention: TestXxx where X is uppercase, or file ends with _test.go
        if (name.StartsWith("Test") && name.Length > 4 && char.IsUpper(name[4]))
            return true;
        return _isTestFile && name.StartsWith("Test");
    }

    private void ExtractFunction(global::TreeSitter.Node node)
    {
        var name = GetNameText(node);
        if (name is null) return;

        var fqn = $"{_packageName}.{name}";
        var isTest = IsGoTestFunction(name);

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

        _relationships.Add(new Relationship(_packageName, fqn, RelationshipType.Declares));

        var body = node.GetChildForField("body");
        if (body is not null)
        {
            _relationships.AddRange(ConfigAccessDetector.DetectGo(body, fqn));
            _relationships.AddRange(HttpCallDetector.DetectGo(body, fqn));
            WalkCallsInBody(body, fqn);
        }
    }

    private void ExtractMethod(global::TreeSitter.Node node)
    {
        var name = GetNameText(node);
        if (name is null) return;

        // Extract receiver type: func (s *Service) Method()
        var receiverType = ExtractReceiverType(node);
        var containingFqn = receiverType is not null ? $"{_packageName}.{receiverType}" : _packageName;
        var fqn = $"{containingFqn}.{name}";

        var isTest = IsGoTestFunction(name);

        _symbols.Add(new MethodInfo
        {
            Fqn = fqn, Name = name, Kind = "method",
            FilePath = _filePath,
            StartLine = (int)node.StartPosition.Row + 1,
            EndLine = (int)node.EndPosition.Row + 1,
            Signature = BuildMethodSignature(name, node, receiverType),
            IsTestMethod = isTest,
            ContainingTypeFqn = containingFqn,
            Documentation = DocCommentHelper.GetPrecedingDocComment(node),
        });

        if (isTest) _testMethodFqns.Add(fqn);

        _relationships.Add(new Relationship(containingFqn, fqn, RelationshipType.HasMethod));

        var body = node.GetChildForField("body");
        if (body is not null)
        {
            _relationships.AddRange(ConfigAccessDetector.DetectGo(body, fqn));
            _relationships.AddRange(HttpCallDetector.DetectGo(body, fqn));
            WalkCallsInBody(body, fqn);
        }
    }

    private void ExtractTypeDeclaration(global::TreeSitter.Node node)
    {
        // type_declaration contains type_spec children
        foreach (var child in node.Children)
        {
            if (child.Type == "type_spec")
                ExtractTypeSpec(child);
        }
    }

    private void ExtractTypeSpec(global::TreeSitter.Node node)
    {
        var name = GetNameText(node);
        if (name is null) return;

        var fqn = $"{_packageName}.{name}";
        var typeNode = node.GetChildForField("type");
        if (typeNode is null) return;

        // Doc comment precedes the type_declaration parent, not the type_spec
        var docNode = node.Parent ?? node;
        var doc = DocCommentHelper.GetPrecedingDocComment(docNode);

        switch (typeNode.Type)
        {
            case "struct_type":
                _symbols.Add(new ClassInfo
                {
                    Fqn = fqn, Name = name, Kind = "struct",
                    FilePath = _filePath,
                    StartLine = (int)node.StartPosition.Row + 1,
                    EndLine = (int)node.EndPosition.Row + 1,
                    Documentation = doc,
                });
                _relationships.Add(new Relationship(_packageName, fqn, RelationshipType.Declares));

                // Extract struct fields
                var fieldList = FindChildByType(typeNode, "field_declaration_list");
                if (fieldList is not null)
                    ExtractStructFields(fieldList, fqn);
                break;

            case "interface_type":
                _symbols.Add(new InterfaceInfo
                {
                    Fqn = fqn, Name = name, Kind = "interface",
                    FilePath = _filePath,
                    StartLine = (int)node.StartPosition.Row + 1,
                    EndLine = (int)node.EndPosition.Row + 1,
                    Documentation = doc,
                });
                _relationships.Add(new Relationship(_packageName, fqn, RelationshipType.Declares));
                break;

            default:
                // Type alias or other
                _symbols.Add(new ClassInfo
                {
                    Fqn = fqn, Name = name, Kind = "type",
                    FilePath = _filePath,
                    StartLine = (int)node.StartPosition.Row + 1,
                    EndLine = (int)node.EndPosition.Row + 1,
                    Documentation = doc,
                });
                _relationships.Add(new Relationship(_packageName, fqn, RelationshipType.Declares));
                break;
        }
    }

    private void ExtractStructFields(global::TreeSitter.Node fieldList, string structFqn)
    {
        foreach (var field in fieldList.Children)
        {
            if (field.Type != "field_declaration") continue;

            var nameNode = FindChildByType(field, "field_identifier");
            var typeNode = field.GetChildForField("type");

            if (nameNode is not null)
            {
                var fieldName = NodeText(nameNode);
                var fieldFqn = $"{structFqn}.{fieldName}";
                var fieldType = typeNode is not null ? NodeText(typeNode) : "any";

                _symbols.Add(new PropertyInfo
                {
                    Fqn = fieldFqn, Name = fieldName, Kind = "property",
                    FilePath = _filePath,
                    StartLine = (int)field.StartPosition.Row + 1,
                    EndLine = (int)field.EndPosition.Row + 1,
                    Type = fieldType,
                    ContainingTypeFqn = structFqn,
                });
                _relationships.Add(new Relationship(structFqn, fieldFqn, RelationshipType.HasProperty));
            }
        }
    }

    private void ExtractImport(global::TreeSitter.Node node)
    {
        // import "fmt" or import ( "fmt" \n "os" )
        foreach (var child in node.Children)
        {
            if (child.Type == "import_spec")
            {
                var pathNode = child.GetChildForField("path");
                if (pathNode is not null)
                {
                    var importPath = NodeText(pathNode).Trim('"');
                    _relationships.Add(new Relationship(
                        _packageName, importPath, RelationshipType.DependsOn,
                        new Dictionary<string, string> { ["importPath"] = importPath }));
                }
            }
            else if (child.Type == "import_spec_list")
            {
                foreach (var spec in child.Children)
                {
                    if (spec.Type == "import_spec")
                    {
                        var pathNode = spec.GetChildForField("path");
                        if (pathNode is not null)
                        {
                            var importPath = NodeText(pathNode).Trim('"');
                            _relationships.Add(new Relationship(
                                _packageName, importPath, RelationshipType.DependsOn,
                                new Dictionary<string, string> { ["importPath"] = importPath }));
                        }
                    }
                }
            }
        }
    }

    private void ExtractCall(global::TreeSitter.Node node)
    {
        var functionNode = node.GetChildForField("function");
        if (functionNode is null) return;

        var calleeName = NodeText(functionNode);
        // Top-level calls outside functions — not in a test context
        _relationships.Add(new Relationship(_packageName, calleeName, RelationshipType.Calls));
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

    private string? ExtractReceiverType(global::TreeSitter.Node node)
    {
        var receiverNode = node.GetChildForField("receiver");
        if (receiverNode is null) return null;

        // Look for type_identifier or pointer_type → type_identifier
        foreach (var child in receiverNode.Children)
        {
            if (child.Type == "parameter_declaration")
            {
                var typeNode = child.GetChildForField("type");
                if (typeNode is null) continue;

                // Unwrap pointer: *Service → Service
                if (typeNode.Type == "pointer_type")
                {
                    var inner = FindChildByType(typeNode, "type_identifier");
                    return inner is not null ? NodeText(inner) : null;
                }
                if (typeNode.Type == "type_identifier")
                    return NodeText(typeNode);
            }
        }
        return null;
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

    private static string BuildSignature(string name, global::TreeSitter.Node node)
    {
        var paramsNode = node.GetChildForField("parameters");
        var resultNode = node.GetChildForField("result");
        var paramText = paramsNode is not null ? NodeText(paramsNode) : "()";
        var retText = resultNode is not null ? $" {NodeText(resultNode)}" : "";
        return $"func {name}{paramText}{retText}";
    }

    private static string BuildMethodSignature(string name, global::TreeSitter.Node node, string? receiverType)
    {
        var paramsNode = node.GetChildForField("parameters");
        var resultNode = node.GetChildForField("result");
        var paramText = paramsNode is not null ? NodeText(paramsNode) : "()";
        var retText = resultNode is not null ? $" {NodeText(resultNode)}" : "";
        var recText = receiverType is not null ? $"({receiverType}) " : "";
        return $"func {recText}{name}{paramText}{retText}";
    }
}
