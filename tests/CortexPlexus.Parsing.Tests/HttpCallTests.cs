using CortexPlexus.Core.Models;
using CortexPlexus.Parsing.TreeSitter;

namespace CortexPlexus.Parsing.Tests;

public sealed class HttpCallDetectorTypeScriptTests
{
    [Fact]
    public void DetectsFetch()
    {
        var rels = ParseTsHttp("""
            async function loadData() {
                const res = await fetch("https://api.example.com/users");
            }
            """, "app.loadData");

        Assert.Contains(rels, r => r.Type == RelationshipType.HttpCalls && r.ToFqn.Contains("api.example.com"));
    }

    [Fact]
    public void DetectsAxiosGet()
    {
        var rels = ParseTsHttp("""
            async function getUser() {
                const res = await axios.get("https://api.example.com/user/1");
            }
            """, "app.getUser");

        Assert.Contains(rels, r => r.Type == RelationshipType.HttpCalls && r.ToFqn.Contains("api.example.com"));
    }

    private static List<Relationship> ParseTsHttp(string code, string callerFqn)
    {
        var lang = new global::TreeSitter.Language("typescript");
        using var parser = new global::TreeSitter.Parser(lang);
        using var tree = parser.Parse(code);
        return HttpCallDetector.DetectTypeScript(tree.RootNode, callerFqn);
    }
}

public sealed class HttpCallDetectorPythonTests
{
    [Fact]
    public void DetectsRequestsGet()
    {
        var rels = ParsePyHttp("""
            import requests
            def get_users():
                r = requests.get("https://api.example.com/users")
            """, "mymodule.get_users");

        Assert.Contains(rels, r => r.Type == RelationshipType.HttpCalls && r.ToFqn.Contains("api.example.com"));
    }

    [Fact]
    public void DetectsRequestsPost()
    {
        var rels = ParsePyHttp("""
            import requests
            def create_user():
                r = requests.post("https://api.example.com/users")
            """, "mymodule.create_user");

        Assert.Contains(rels, r =>
            r.Type == RelationshipType.HttpCalls &&
            r.Metadata != null && r.Metadata["httpMethod"] == "POST");
    }

    private static List<Relationship> ParsePyHttp(string code, string callerFqn)
    {
        var lang = new global::TreeSitter.Language("python");
        using var parser = new global::TreeSitter.Parser(lang);
        using var tree = parser.Parse(code);
        return HttpCallDetector.DetectPython(tree.RootNode, callerFqn);
    }
}

public sealed class HttpCallDetectorGoTests
{
    [Fact]
    public void DetectsHttpGet()
    {
        var rels = ParseGoHttp("""
            package main
            import "net/http"
            func fetchData() {
                resp, err := http.Get("https://api.example.com/data")
            }
            """, "main.fetchData");

        Assert.Contains(rels, r => r.Type == RelationshipType.HttpCalls && r.ToFqn.Contains("api.example.com"));
    }

    private static List<Relationship> ParseGoHttp(string code, string callerFqn)
    {
        var lang = new global::TreeSitter.Language("go");
        using var parser = new global::TreeSitter.Parser(lang);
        using var tree = parser.Parse(code);
        return HttpCallDetector.DetectGo(tree.RootNode, callerFqn);
    }
}

public sealed class HttpCallDetectorRustTests
{
    [Fact]
    public void DetectsReqwestGet()
    {
        var rels = ParseRustHttp("""
            async fn fetch() {
                let body = reqwest::get("https://api.example.com/data").await;
            }
            """, "crate::fetch");

        Assert.Contains(rels, r => r.Type == RelationshipType.HttpCalls && r.ToFqn.Contains("api.example.com"));
    }

    private static List<Relationship> ParseRustHttp(string code, string callerFqn)
    {
        var lang = new global::TreeSitter.Language("rust");
        using var parser = new global::TreeSitter.Parser(lang);
        using var tree = parser.Parse(code);
        return HttpCallDetector.DetectRust(tree.RootNode, callerFqn);
    }
}
