using CortexPlexus.Core.Models;
using CortexPlexus.Parsing.TreeSitter;

namespace CortexPlexus.Parsing.Tests;

// ============================================================
// EventEmitter detection — JS/TS
// ============================================================

public sealed class EventPatternDetectorTypeScriptTests
{
    [Fact]
    public void DetectsEmitterOn()
    {
        var rels = ParseTsEvents("""
            function setup(emitter) {
                emitter.on("data", (chunk) => console.log(chunk));
            }
            """, "app.setup");

        Assert.Contains(rels, r =>
            r.Type == RelationshipType.Subscribes &&
            r.ToFqn == "event:data");
    }

    [Fact]
    public void DetectsEmitterEmit()
    {
        var rels = ParseTsEvents("""
            function notify(emitter) {
                emitter.emit("orderCreated", { id: 1 });
            }
            """, "app.notify");

        Assert.Contains(rels, r =>
            r.Type == RelationshipType.Publishes &&
            r.ToFqn == "event:orderCreated");
    }

    [Fact]
    public void DetectsAddEventListener()
    {
        var rels = ParseTsEvents("""
            function init() {
                document.addEventListener("click", handleClick);
            }
            """, "app.init");

        Assert.Contains(rels, r =>
            r.Type == RelationshipType.Subscribes &&
            r.ToFqn == "event:click");
    }

    [Fact]
    public void NoDuplicates()
    {
        var rels = ParseTsEvents("""
            function init(e) {
                e.on("data", () => {});
                e.on("data", () => {});
            }
            """, "app.init");

        var dataRels = rels.Where(r => r.ToFqn == "event:data").ToList();
        Assert.Single(dataRels);
    }

    private static List<Relationship> ParseTsEvents(string code, string callerFqn)
    {
        var lang = new global::TreeSitter.Language("typescript");
        using var parser = new global::TreeSitter.Parser(lang);
        using var tree = parser.Parse(code);
        return EventPatternDetector.DetectTypeScript(tree.RootNode, callerFqn);
    }
}

// ============================================================
// Signal detection — Python
// ============================================================

public sealed class EventPatternDetectorPythonTests
{
    [Fact]
    public void DetectsSignalConnect()
    {
        var rels = ParsePyEvents("""
            def setup():
                post_save.connect(on_save)
            """, "mymodule.setup");

        Assert.Contains(rels, r =>
            r.Type == RelationshipType.Subscribes &&
            r.ToFqn == "event:post_save");
    }

    [Fact]
    public void DetectsSignalSend()
    {
        var rels = ParsePyEvents("""
            def notify():
                order_created.send(sender=self)
            """, "mymodule.notify");

        Assert.Contains(rels, r =>
            r.Type == RelationshipType.Publishes &&
            r.ToFqn == "event:order_created");
    }

    private static List<Relationship> ParsePyEvents(string code, string callerFqn)
    {
        var lang = new global::TreeSitter.Language("python");
        using var parser = new global::TreeSitter.Parser(lang);
        using var tree = parser.Parse(code);
        return EventPatternDetector.DetectPython(tree.RootNode, callerFqn);
    }
}
