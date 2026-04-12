using CortexPlexus.Core.Models;

namespace CortexPlexus.Parsing.TreeSitter;

/// <summary>
/// Detects outgoing HTTP calls across languages via Tree-sitter AST.
/// Creates HttpCalls edges from the caller to "http:URL".
///
/// Patterns per language:
/// - JS/TS: fetch("url"), axios.get("url"), axios.post("url")
/// - Python: requests.get("url"), requests.post("url"), urllib.request.urlopen("url"), httpx.get("url")
/// - Java: URL("url").openConnection(), HttpRequest.newBuilder().uri(URI.create("url"))
/// - Go: http.Get("url"), http.Post("url"), http.NewRequest("METHOD", "url", ...)
/// - Rust: reqwest::get("url"), Client::new().get("url")
/// - PHP: file_get_contents("url"), curl_init("url")
/// </summary>
internal static class HttpCallDetector
{
    public static List<Relationship> DetectTypeScript(global::TreeSitter.Node root, string callerFqn)
    {
        var results = new List<Relationship>();
        var seen = new HashSet<string>();
        WalkForTypeScript(root, callerFqn, results, seen);
        return results;
    }

    public static List<Relationship> DetectPython(global::TreeSitter.Node root, string callerFqn)
    {
        var results = new List<Relationship>();
        var seen = new HashSet<string>();
        WalkForPython(root, callerFqn, results, seen);
        return results;
    }

    public static List<Relationship> DetectJava(global::TreeSitter.Node root, string callerFqn)
    {
        var results = new List<Relationship>();
        var seen = new HashSet<string>();
        WalkForJava(root, callerFqn, results, seen);
        return results;
    }

    public static List<Relationship> DetectGo(global::TreeSitter.Node root, string callerFqn)
    {
        var results = new List<Relationship>();
        var seen = new HashSet<string>();
        WalkForGo(root, callerFqn, results, seen);
        return results;
    }

    public static List<Relationship> DetectRust(global::TreeSitter.Node root, string callerFqn)
    {
        var results = new List<Relationship>();
        var seen = new HashSet<string>();
        WalkForRust(root, callerFqn, results, seen);
        return results;
    }

    public static List<Relationship> DetectPhp(global::TreeSitter.Node root, string callerFqn)
    {
        var results = new List<Relationship>();
        var seen = new HashSet<string>();
        WalkForPhp(root, callerFqn, results, seen);
        return results;
    }

    // --- JS/TS: fetch("url"), axios.get("url"), axios.post("url") ---

    private static void WalkForTypeScript(global::TreeSitter.Node node, string callerFqn,
        List<Relationship> results, HashSet<string> seen)
    {
        if (node.Type == "call_expression")
        {
            var funcNode = node.GetChildForField("function");
            var funcText = funcNode?.Text ?? "";

            // fetch("url")
            if (funcText == "fetch")
            {
                var url = ExtractFirstStringArg(node);
                if (url is not null)
                    AddHttpEdge(callerFqn, url, "GET", results, seen);
            }

            // axios.get("url"), axios.post("url"), etc.
            if (funcText.StartsWith("axios.") || funcText.StartsWith("http."))
            {
                var method = funcText.Split('.').LastOrDefault()?.ToUpperInvariant() ?? "HTTP";
                var url = ExtractFirstStringArg(node);
                if (url is not null)
                    AddHttpEdge(callerFqn, url, method, results, seen);
            }
        }

        foreach (var child in node.Children)
            WalkForTypeScript(child, callerFqn, results, seen);
    }

    // --- Python: requests.get("url"), httpx.get("url"), urllib ---

    private static void WalkForPython(global::TreeSitter.Node node, string callerFqn,
        List<Relationship> results, HashSet<string> seen)
    {
        if (node.Type == "call")
        {
            var funcNode = node.GetChildForField("function");
            var funcText = funcNode?.Text ?? "";

            // requests.get("url"), requests.post("url"), httpx.get("url")
            if (funcText.StartsWith("requests.") || funcText.StartsWith("httpx."))
            {
                var method = funcText.Split('.').LastOrDefault()?.ToUpperInvariant() ?? "HTTP";
                var url = ExtractFirstStringArgPython(node);
                if (url is not null)
                    AddHttpEdge(callerFqn, url, method, results, seen);
            }

            // urllib.request.urlopen("url")
            if (funcText.Contains("urlopen"))
            {
                var url = ExtractFirstStringArgPython(node);
                if (url is not null)
                    AddHttpEdge(callerFqn, url, "GET", results, seen);
            }
        }

        foreach (var child in node.Children)
            WalkForPython(child, callerFqn, results, seen);
    }

    // --- Java: HttpRequest, URL.openConnection ---

    private static void WalkForJava(global::TreeSitter.Node node, string callerFqn,
        List<Relationship> results, HashSet<string> seen)
    {
        if (node.Type == "method_invocation")
        {
            var nameNode = node.GetChildForField("name");
            var methodName = nameNode?.Text ?? "";

            // URI.create("url") in HttpRequest context
            if (methodName == "create")
            {
                var objNode = node.GetChildForField("object");
                if (objNode?.Text == "URI")
                {
                    var url = ExtractFirstStringArgJava(node);
                    if (url is not null)
                        AddHttpEdge(callerFqn, url, "HTTP", results, seen);
                }
            }
        }

        // new URL("url")
        if (node.Type == "object_creation_expression")
        {
            var typeNode = node.GetChildForField("type");
            if (typeNode?.Text == "URL")
            {
                var args = node.GetChildForField("arguments");
                if (args is not null)
                {
                    var url = FindStringContent(args);
                    if (url is not null)
                        AddHttpEdge(callerFqn, url, "HTTP", results, seen);
                }
            }
        }

        foreach (var child in node.Children)
            WalkForJava(child, callerFqn, results, seen);
    }

    // --- Go: http.Get("url"), http.Post("url"), http.NewRequest("GET", "url", ...) ---

    private static void WalkForGo(global::TreeSitter.Node node, string callerFqn,
        List<Relationship> results, HashSet<string> seen)
    {
        if (node.Type == "call_expression")
        {
            var funcNode = node.GetChildForField("function");
            var funcText = funcNode?.Text ?? "";

            if (funcText is "http.Get" or "http.Head")
            {
                var url = ExtractFirstStringArgGo(node);
                if (url is not null)
                    AddHttpEdge(callerFqn, url, "GET", results, seen);
            }
            else if (funcText is "http.Post" or "http.PostForm")
            {
                var url = ExtractFirstStringArgGo(node);
                if (url is not null)
                    AddHttpEdge(callerFqn, url, "POST", results, seen);
            }
            else if (funcText == "http.NewRequest")
            {
                var args = node.GetChildForField("arguments");
                if (args is not null)
                {
                    var namedChildren = GetNamedChildren(args);
                    if (namedChildren.Count >= 2)
                    {
                        var method = StripQuotes(namedChildren[0].Text ?? "HTTP");
                        var url = StripQuotes(namedChildren[1].Text ?? "unknown");
                        if (!string.IsNullOrEmpty(url))
                            AddHttpEdge(callerFqn, url, method.ToUpperInvariant(), results, seen);
                    }
                }
            }
        }

        foreach (var child in node.Children)
            WalkForGo(child, callerFqn, results, seen);
    }

    // --- Rust: reqwest::get("url"), Client::new().get("url") ---

    private static void WalkForRust(global::TreeSitter.Node node, string callerFqn,
        List<Relationship> results, HashSet<string> seen)
    {
        if (node.Type == "call_expression")
        {
            var funcNode = node.GetChildForField("function");
            var funcText = funcNode?.Text ?? "";

            if (funcText is "reqwest::get" or "reqwest::blocking::get")
            {
                var url = ExtractFirstStringArgRust(node);
                if (url is not null)
                    AddHttpEdge(callerFqn, url, "GET", results, seen);
            }

            // .get("url"), .post("url") on client
            if (funcText.EndsWith(".get") || funcText.EndsWith(".post") ||
                funcText.EndsWith(".put") || funcText.EndsWith(".delete"))
            {
                var method = funcText.Split('.').LastOrDefault()?.ToUpperInvariant() ?? "HTTP";
                var url = ExtractFirstStringArgRust(node);
                if (url is not null)
                    AddHttpEdge(callerFqn, url, method, results, seen);
            }
        }

        foreach (var child in node.Children)
            WalkForRust(child, callerFqn, results, seen);
    }

    // --- PHP: file_get_contents("url"), curl_init("url") ---

    private static void WalkForPhp(global::TreeSitter.Node node, string callerFqn,
        List<Relationship> results, HashSet<string> seen)
    {
        if (node.Type == "function_call_expression")
        {
            var funcNode = node.GetChildForField("name");
            var funcText = funcNode?.Text ?? "";

            if (funcText is "file_get_contents" or "curl_init")
            {
                var args = node.GetChildForField("arguments");
                if (args is not null)
                {
                    var url = FindStringContent(args);
                    if (url is not null && (url.StartsWith("http://") || url.StartsWith("https://")))
                        AddHttpEdge(callerFqn, url, funcText == "curl_init" ? "HTTP" : "GET", results, seen);
                }
            }
        }

        foreach (var child in node.Children)
            WalkForPhp(child, callerFqn, results, seen);
    }

    // --- Helpers ---

    private static void AddHttpEdge(string callerFqn, string url, string httpMethod,
        List<Relationship> results, HashSet<string> seen)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        var targetFqn = $"http:{url}";
        var key = $"{callerFqn}->{targetFqn}:{httpMethod}";
        if (!seen.Add(key)) return;

        results.Add(new Relationship(
            callerFqn, targetFqn, RelationshipType.HttpCalls,
            new Dictionary<string, string>
            {
                ["httpMethod"] = httpMethod,
                ["url"] = url,
            }));
    }

    private static string? ExtractFirstStringArg(global::TreeSitter.Node callNode)
    {
        var args = callNode.GetChildForField("arguments");
        if (args is null) return null;
        var first = GetFirstNamedChild(args);
        return first is not null ? TryGetString(first) : null;
    }

    private static string? ExtractFirstStringArgPython(global::TreeSitter.Node callNode)
    {
        var args = callNode.GetChildForField("arguments");
        if (args is null) return null;
        var first = GetFirstNamedChild(args);
        return first is not null ? TryGetString(first) : null;
    }

    private static string? ExtractFirstStringArgJava(global::TreeSitter.Node callNode)
    {
        var args = callNode.GetChildForField("arguments");
        if (args is null) return null;
        var first = GetFirstNamedChild(args);
        return first is not null ? TryGetString(first) : null;
    }

    private static string? ExtractFirstStringArgGo(global::TreeSitter.Node callNode)
    {
        var args = callNode.GetChildForField("arguments");
        if (args is null) return null;
        var first = GetFirstNamedChild(args);
        return first is not null ? TryGetString(first) : null;
    }

    private static string? ExtractFirstStringArgRust(global::TreeSitter.Node callNode)
    {
        var args = callNode.GetChildForField("arguments");
        if (args is null) return null;
        var first = GetFirstNamedChild(args);
        return first is not null ? TryGetString(first) : null;
    }

    private static string? TryGetString(global::TreeSitter.Node node)
    {
        var text = node.Text ?? "";
        // Direct string content
        if (node.Type is "string" or "string_literal" or "interpreted_string_literal"
            or "string_content" or "encapsed_string" or "template_string")
        {
            return StripQuotes(text);
        }
        // Quoted text
        if (text.Length >= 2 && ((text[0] == '"' && text[^1] == '"') || (text[0] == '\'' && text[^1] == '\'')))
            return text[1..^1];
        return null;
    }

    private static string? FindStringContent(global::TreeSitter.Node node)
    {
        if (node.Type == "string_content")
            return node.Text;
        var str = TryGetString(node);
        if (str is not null) return str;
        foreach (var child in node.Children)
        {
            var result = FindStringContent(child);
            if (result is not null) return result;
        }
        return null;
    }

    private static string StripQuotes(string text)
    {
        if (text.Length < 2) return text;
        if ((text[0] == '"' && text[^1] == '"') || (text[0] == '\'' && text[^1] == '\'') || (text[0] == '`' && text[^1] == '`'))
            return text[1..^1];
        return text;
    }

    private static global::TreeSitter.Node? GetFirstNamedChild(global::TreeSitter.Node node)
    {
        foreach (var child in node.Children)
            if (child.IsNamed) return child;
        return null;
    }

    private static List<global::TreeSitter.Node> GetNamedChildren(global::TreeSitter.Node node)
    {
        var result = new List<global::TreeSitter.Node>();
        foreach (var child in node.Children)
            if (child.IsNamed) result.Add(child);
        return result;
    }
}
