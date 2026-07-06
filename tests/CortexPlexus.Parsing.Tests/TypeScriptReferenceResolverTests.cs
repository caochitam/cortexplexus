using CortexPlexus.Core.Models;
using CortexPlexus.Parsing.TreeSitter;
using Microsoft.Extensions.Logging.Abstractions;

namespace CortexPlexus.Parsing.Tests;

/// <summary>
/// Tests for the whole-repo TypeScript reference resolver — the upgrade that makes the
/// call/dependency graph tools (get_callers, get_dependencies, get_impact_analysis, …) work on
/// a TypeScript codebase by rewriting bare call names and raw import specifiers to real vertex
/// FQNs. Three layers: pure resolver logic, end-to-end through the tree-sitter parser on a temp
/// fixture, and a live pass over the real legacy-platform repo (skipped when absent).
/// </summary>
public sealed class TypeScriptReferenceResolverTests
{
    // ---------- Layer 1: pure resolver logic (no filesystem, no tree-sitter) ----------

    [Fact]
    public void Resolve_AliasImport_RewritesBareCallToDefinitionFqn()
    {
        var symbols = new List<CodeSymbol>
        {
            Module("src/lib/rate-plan-matrix.ts"),
            Fn("src/lib/rate-plan-matrix.ts:getTicketPrice", "getTicketPrice", exported: true),
            Module("src/app/slot.ts"),
            Fn("src/app/slot.ts:handler", "handler"),
        };
        var rels = new List<Relationship>
        {
            new("src/app/slot.ts", "@/lib/rate-plan-matrix", RelationshipType.DependsOn,
                new() { ["importPath"] = "@/lib/rate-plan-matrix", ["names"] = "getTicketPrice" }),
            // Call attributed to the module (the extractor scopes top-level bodies to the file).
            new("src/app/slot.ts", "getTicketPrice", RelationshipType.Calls),
        };

        var outRels = new TypeScriptReferenceResolver(new[] { ("@/", "src") }).Resolve(symbols, rels);

        Assert.Contains(outRels, r => r.Type == RelationshipType.Calls
            && r.FromFqn == "src/app/slot.ts" && r.ToFqn == "src/lib/rate-plan-matrix.ts:getTicketPrice");
        Assert.Contains(outRels, r => r.Type == RelationshipType.DependsOn
            && r.FromFqn == "src/app/slot.ts" && r.ToFqn == "src/lib/rate-plan-matrix.ts");
        // The unresolved bare name must not survive.
        Assert.DoesNotContain(outRels, r => r.Type == RelationshipType.Calls && r.ToFqn == "getTicketPrice");
    }

    [Fact]
    public void Resolve_AliasedNamedImport_MapsLocalNameToOriginalDefinition()
    {
        var symbols = new List<CodeSymbol>
        {
            Module("src/lib/x.ts"),
            Fn("src/lib/x.ts:getTicketPrice", "getTicketPrice", exported: true),
            Module("src/app/a.ts"),
        };
        var rels = new List<Relationship>
        {
            // import { getTicketPrice as gtp } from '@/lib/x'
            new("src/app/a.ts", "@/lib/x", RelationshipType.DependsOn,
                new() { ["importPath"] = "@/lib/x", ["names"] = "gtp=getTicketPrice" }),
            new("src/app/a.ts", "gtp", RelationshipType.Calls),
        };

        var outRels = new TypeScriptReferenceResolver(new[] { ("@/", "src") }).Resolve(symbols, rels);

        Assert.Contains(outRels, r => r.Type == RelationshipType.Calls && r.ToFqn == "src/lib/x.ts:getTicketPrice");
    }

    [Fact]
    public void Resolve_AmbiguousGlobalName_LeftUnresolved()
    {
        // 'helper' is defined in two modules and NOT imported by the caller → resolver must not
        // guess. This is the precision guard that keeps blast-radius from wiring wrong edges.
        var symbols = new List<CodeSymbol>
        {
            Module("src/a.ts"), Fn("src/a.ts:helper", "helper", exported: true),
            Module("src/b.ts"), Fn("src/b.ts:helper", "helper", exported: true),
            Module("src/c.ts"), Fn("src/c.ts:run", "run"),
        };
        var rels = new List<Relationship> { new("src/c.ts", "helper", RelationshipType.Calls) };

        var outRels = new TypeScriptReferenceResolver(Array.Empty<(string, string)>()).Resolve(symbols, rels);

        Assert.Contains(outRels, r => r.Type == RelationshipType.Calls && r.ToFqn == "helper"); // unchanged
    }

    [Fact]
    public void Resolve_NonTsEdges_PassThroughUnchanged()
    {
        var symbols = new List<CodeSymbol> { Module("app/main.py"), Fn("app/main.py:foo", "foo") };
        var rels = new List<Relationship> { new("app/main.py", "bar", RelationshipType.Calls) };

        var outRels = new TypeScriptReferenceResolver(Array.Empty<(string, string)>()).Resolve(symbols, rels);

        Assert.Contains(outRels, r => r.FromFqn == "app/main.py" && r.ToFqn == "bar"); // untouched
    }

    // ---------- Layer 2: end-to-end through the real tree-sitter parser on a temp fixture ----------

    [Fact]
    public async Task ParseSolution_ResolvesStaticRelativeAliasAndDynamicImportCalls()
    {
        var root = CreateFixture(new Dictionary<string, string>
        {
            ["tsconfig.json"] = """{ "compilerOptions": { "paths": { "@/*": ["./src/*"] } } }""",
            ["src/lib/rate-plan-matrix.ts"] = "export async function getTicketPrice(){ return 1; }\n",
            // relative import
            ["src/lib/cash-tiers.ts"] =
                "import { getTicketPrice } from \"./rate-plan-matrix\";\n" +
                "export function computeCash(){ return getTicketPrice(); }\n",
            // @/ alias import
            ["src/app/slot.ts"] =
                "import { getTicketPrice } from \"@/lib/rate-plan-matrix\";\n" +
                "export function handler(){ return getTicketPrice(); }\n",
            // dynamic import (bindings invisible to the AST walk → resolved via unique-export fallback)
            ["src/app/bookings.ts"] =
                "export async function post(){\n" +
                "  const { getTicketPrice } = await import(\"@/lib/rate-plan-matrix\");\n" +
                "  return getTicketPrice();\n}\n",
        });

        try
        {
            var parser = new TreeSitterCodeParser(NullLogger<TreeSitterCodeParser>.Instance);
            var rels = (await parser.ParseSolutionAsync(root)).Relationships;

            const string def = "src/lib/rate-plan-matrix.ts:getTicketPrice";
            const string mod = "src/lib/rate-plan-matrix.ts";

            // (1) all three call sites resolve to the real definition FQN
            var callers = rels.Where(r => r.Type == RelationshipType.Calls && r.ToFqn == def)
                              .Select(r => r.FromFqn).ToHashSet();
            Assert.Contains("src/lib/cash-tiers.ts", callers);  // relative
            Assert.Contains("src/app/slot.ts", callers);        // @/ alias
            Assert.Contains("src/app/bookings.ts", callers);    // dynamic import + fallback

            // (2) no unresolved bare call remains
            Assert.DoesNotContain(rels, r => r.Type == RelationshipType.Calls && r.ToFqn == "getTicketPrice");

            // (3) import specifiers (static + dynamic) resolve to the module vertex
            Assert.Contains(rels, r => r.Type == RelationshipType.DependsOn && r.FromFqn == "src/lib/cash-tiers.ts" && r.ToFqn == mod);
            Assert.Contains(rels, r => r.Type == RelationshipType.DependsOn && r.FromFqn == "src/app/slot.ts" && r.ToFqn == mod);
            Assert.Contains(rels, r => r.Type == RelationshipType.DependsOn && r.FromFqn == "src/app/bookings.ts" && r.ToFqn == mod);

            // (4) raw specifiers must not survive as dangling edges
            Assert.DoesNotContain(rels, r => r.Type == RelationshipType.DependsOn && r.ToFqn == "@/lib/rate-plan-matrix");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // ---------- Layer 3: live pass over the real legacy-platform repo (skipped when absent) ----------

    [Fact]
    public async Task ParseSolution_ResolvesGetTicketPriceInRealLegacyPlatform()
    {
        const string repo = "/home/doctorcity_com/Projects/legacy-platform";
        if (!Directory.Exists(repo)) return; // environment-specific proof; no-op elsewhere

        var parser = new TreeSitterCodeParser(NullLogger<TreeSitterCodeParser>.Instance);
        var result = await parser.ParseSolutionAsync(repo);
        var rels = result.Relationships;

        const string def = "src/lib/rate-plan-matrix.ts:getTicketPrice";

        Assert.Contains(result.Symbols, s => s.Fqn == def);

        var resolvedCallers = rels
            .Where(r => r.Type == RelationshipType.Calls && r.ToFqn == def)
            .Select(r => r.FromFqn).Distinct().Count();
        Assert.True(resolvedCallers >= 3,
            $"expected >= 3 resolved callers of getTicketPrice across legacy-platform, got {resolvedCallers}");

        var moduleDeps = rels.Count(r => r.Type == RelationshipType.DependsOn && r.ToFqn == "src/lib/rate-plan-matrix.ts");
        Assert.True(moduleDeps >= 1,
            "expected @/ or relative imports of rate-plan-matrix to resolve to the module FQN");
    }

    // ---------- helpers ----------

    private static NamespaceInfo Module(string fqn) => new()
    {
        Fqn = fqn, Name = fqn.Split('/').Last(), Kind = "module", FilePath = fqn,
    };

    private static MethodInfo Fn(string fqn, string name, bool exported = false) => new()
    {
        Fqn = fqn, Name = name, Kind = "function", Signature = $"{name}()",
        FilePath = fqn.Split(':')[0], Accessibility = exported ? "export" : null,
    };

    private static string CreateFixture(Dictionary<string, string> files)
    {
        var root = Path.Combine(Path.GetTempPath(), "cortex-tsres-" + Guid.NewGuid().ToString("N"));
        foreach (var (rel, content) in files)
        {
            var full = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }
        return root;
    }
}
