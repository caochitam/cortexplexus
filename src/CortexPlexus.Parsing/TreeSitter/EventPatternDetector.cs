using CortexPlexus.Core.Models;

namespace CortexPlexus.Parsing.TreeSitter;

/// <summary>
/// Detects event/messaging patterns across languages via Tree-sitter AST.
///
/// Patterns per language:
/// - JS/TS: emitter.on("event", handler) → Subscribes, emitter.emit("event") → Publishes
/// - Python: signal.connect(handler) → Subscribes, signal.send() → Publishes
/// </summary>
internal static class EventPatternDetector
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

    // --- JS/TS: .on("event"), .emit("event"), .addEventListener("event"), .removeEventListener ---

    private static void WalkForTypeScript(global::TreeSitter.Node node, string callerFqn,
        List<Relationship> results, HashSet<string> seen)
    {
        if (node.Type == "call_expression")
        {
            var funcNode = node.GetChildForField("function");
            var funcText = funcNode?.Text ?? "";

            // .on("event", handler), .addListener("event", handler), .addEventListener("event", handler)
            if (funcText.EndsWith(".on") || funcText.EndsWith(".addListener") || funcText.EndsWith(".addEventListener"))
            {
                var eventName = ExtractFirstStringArg(node);
                if (eventName is not null)
                    AddEdge(callerFqn, $"event:{eventName}", RelationshipType.Subscribes, results, seen);
            }

            // .emit("event", ...), .dispatchEvent(...)
            if (funcText.EndsWith(".emit") || funcText.EndsWith(".dispatchEvent"))
            {
                var eventName = ExtractFirstStringArg(node);
                if (eventName is not null)
                    AddEdge(callerFqn, $"event:{eventName}", RelationshipType.Publishes, results, seen);
            }

            // .off("event", handler), .removeListener("event", handler), .removeEventListener("event", handler)
            // We don't track unsubscriptions — only subscriptions matter for the graph
        }

        foreach (var child in node.Children)
            WalkForTypeScript(child, callerFqn, results, seen);
    }

    // --- Python: signal.connect(handler), signal.send(), @receiver(signal) ---

    private static void WalkForPython(global::TreeSitter.Node node, string callerFqn,
        List<Relationship> results, HashSet<string> seen)
    {
        if (node.Type == "call")
        {
            var funcNode = node.GetChildForField("function");
            var funcText = funcNode?.Text ?? "";

            // signal.connect(handler) — Django signals, blinker
            if (funcText.EndsWith(".connect"))
            {
                var signalName = funcText.Replace(".connect", "");
                if (!string.IsNullOrEmpty(signalName))
                    AddEdge(callerFqn, $"event:{signalName}", RelationshipType.Subscribes, results, seen);
            }

            // signal.send(sender), signal.emit()
            if (funcText.EndsWith(".send") || funcText.EndsWith(".emit"))
            {
                var signalName = funcText.Replace(".send", "").Replace(".emit", "");
                if (!string.IsNullOrEmpty(signalName))
                    AddEdge(callerFqn, $"event:{signalName}", RelationshipType.Publishes, results, seen);
            }
        }

        foreach (var child in node.Children)
            WalkForPython(child, callerFqn, results, seen);
    }

    // --- Helpers ---

    private static void AddEdge(string callerFqn, string eventFqn, RelationshipType type,
        List<Relationship> results, HashSet<string> seen)
    {
        if (string.IsNullOrWhiteSpace(eventFqn)) return;
        var key = $"{callerFqn}->{eventFqn}:{type}";
        if (!seen.Add(key)) return;

        results.Add(new Relationship(callerFqn, eventFqn, type,
            new Dictionary<string, string> { ["provider"] = "EventEmitter" }));
    }

    private static string? ExtractFirstStringArg(global::TreeSitter.Node callNode)
    {
        var args = callNode.GetChildForField("arguments");
        if (args is null) return null;

        foreach (var child in args.Children)
        {
            if (!child.IsNamed) continue;
            var text = child.Text ?? "";
            if (text.Length >= 2 && ((text[0] == '"' && text[^1] == '"') || (text[0] == '\'' && text[^1] == '\'')))
                return text[1..^1];
            if (child.Type is "string" or "template_string")
                return StripQuotes(text);
            break; // Only check first argument
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
}
