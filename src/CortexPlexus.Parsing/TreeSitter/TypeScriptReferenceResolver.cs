using System.Text.RegularExpressions;
using CortexPlexus.Core.Models;

namespace CortexPlexus.Parsing.TreeSitter;

/// <summary>
/// Whole-repo, post-parse resolution pass for TypeScript / JavaScript.
///
/// The per-file <see cref="TypeScriptExtractor"/> emits UNRESOLVED endpoints because a single
/// file has no view of the rest of the repo:
///   • a call <c>getTicketPrice(...)</c> becomes a <c>Calls</c> edge to the BARE NAME "getTicketPrice";
///   • an import <c>from '@/lib/x'</c> becomes a <c>DependsOn</c> edge to the RAW SPECIFIER "@/lib/x".
/// Neither matches any real vertex FQN (symbols are keyed <c>relativePath:name</c>, modules
/// <c>relativePath</c>), so the call/dependency graph queries (get_callers, get_callees,
/// get_dependencies, get_impact_analysis, get_dead_code, …) return nothing for TypeScript —
/// those queries were built around Roslyn's SemanticModel, which the tree-sitter path lacks.
///
/// This resolver runs once, after every file is parsed, and rewrites those endpoints to real
/// FQNs using, in order of precedence:
///   1. per-file named-import bindings (precise: <c>import { getTicketPrice } from '@/lib/x'</c>);
///   2. same-file local top-level definitions;
///   3. a globally-UNIQUE top-level name fallback — which also recovers dynamic
///      <c>const { getTicketPrice } = await import('@/lib/x')</c> call sites, whose bindings the
///      AST walk can't see, and barrel re-exports. Ambiguous names (defined in &gt;1 module) are
///      left unresolved rather than linked to the wrong definition.
///
/// Module specifiers are resolved with tsconfig path aliases (<c>@/* → src/*</c>), relative-path
/// resolution, and extension / index-file completion. Only TS/JS edges are touched; C#, Python,
/// Go, … relationships pass through unchanged.
/// </summary>
public sealed class TypeScriptReferenceResolver
{
    private static readonly string[] ModuleExtensions =
        { ".ts", ".tsx", ".mts", ".cts", ".js", ".jsx", ".mjs", ".cjs" };

    private readonly List<(string Prefix, string Target)> _aliases;

    /// <summary>Reads tsconfig.json / jsconfig.json path aliases from the repo root.</summary>
    public TypeScriptReferenceResolver(string repoRoot)
    {
        _aliases = LoadTsConfigAliases(repoRoot);
    }

    /// <summary>Test seam: supply aliases directly (prefix, resolvedTargetDir), e.g. ("@/", "src").</summary>
    internal TypeScriptReferenceResolver(IEnumerable<(string Prefix, string Target)> aliases)
    {
        _aliases = aliases.ToList();
    }

    /// <summary>tsconfig aliases actually loaded (for diagnostics / tests).</summary>
    internal IReadOnlyList<(string Prefix, string Target)> Aliases => _aliases;

    public List<Relationship> Resolve(IReadOnlyList<CodeSymbol> symbols, IReadOnlyList<Relationship> relationships)
    {
        // Indexes are scoped to TS/JS symbols only so a same-named Python/Go symbol can't
        // pollute the uniqueness fallback.
        var knownModules = new HashSet<string>(StringComparer.Ordinal);
        var allFqns = new HashSet<string>(StringComparer.Ordinal);
        var localNames = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        var globalNames = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        foreach (var s in symbols)
        {
            if (string.IsNullOrEmpty(s.Fqn) || !IsTsModule(s.Fqn)) continue;
            allFqns.Add(s.Fqn);

            if (s.Kind == "module")
            {
                knownModules.Add(s.Fqn);
                continue;
            }

            var (module, member) = SplitFqn(s.Fqn);
            if (module is null || member is null || member.Contains('.'))
                continue; // nested member (Class.method) is never a bare-identifier call target

            if (!localNames.TryGetValue(module, out var lmap))
                localNames[module] = lmap = new Dictionary<string, string>(StringComparer.Ordinal);
            lmap[member] = s.Fqn;

            if (!globalNames.TryGetValue(member, out var gset))
                globalNames[member] = gset = new HashSet<string>(StringComparer.Ordinal);
            gset.Add(s.Fqn);
        }

        // Per-module named-import bindings: localName → (targetModuleFqn, originalName).
        var bindings = new Dictionary<string, Dictionary<string, (string Module, string Original)>>(StringComparer.Ordinal);
        foreach (var r in relationships)
        {
            if (r.Type != RelationshipType.DependsOn || !IsTsModule(r.FromFqn)) continue;
            if (r.Metadata is null || !r.Metadata.TryGetValue("names", out var namesCsv) || namesCsv.Length == 0) continue;

            var target = ResolveSpecifier(r.ToFqn, r.FromFqn, knownModules);
            if (target is null) continue;

            if (!bindings.TryGetValue(r.FromFqn, out var bmap))
                bindings[r.FromFqn] = bmap = new Dictionary<string, (string, string)>(StringComparer.Ordinal);

            foreach (var entry in namesCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var eq = entry.IndexOf('=');
                var local = eq < 0 ? entry : entry[..eq];
                var original = eq < 0 ? entry : entry[(eq + 1)..];
                bmap[local] = (target, original);
            }
        }

        // Rewrite pass. Edge count is preserved (the graph store dedups on upsert); we only
        // change ToFqn where we can resolve it to a real vertex.
        var result = new List<Relationship>(relationships.Count);
        foreach (var r in relationships)
        {
            if (r.Type == RelationshipType.DependsOn && IsTsModule(r.FromFqn))
            {
                var target = ResolveSpecifier(r.ToFqn, r.FromFqn, knownModules);
                result.Add(target is not null && target != r.ToFqn ? r with { ToFqn = target } : r);
            }
            else if ((r.Type == RelationshipType.Calls || r.Type == RelationshipType.TestCovers)
                     && IsTsModule(r.FromFqn) && IsBareName(r.ToFqn))
            {
                var resolved = ResolveCallee(r.FromFqn, r.ToFqn, bindings, localNames, globalNames, allFqns);
                result.Add(resolved is not null && resolved != r.ToFqn ? r with { ToFqn = resolved } : r);
            }
            else
            {
                result.Add(r);
            }
        }
        return result;
    }

    private static string? ResolveCallee(
        string callerFqn, string name,
        Dictionary<string, Dictionary<string, (string Module, string Original)>> bindings,
        Dictionary<string, Dictionary<string, string>> localNames,
        Dictionary<string, HashSet<string>> globalNames,
        HashSet<string> allFqns)
    {
        var module = ModuleOf(callerFqn);

        // 1. Precise: name came in through a named import.
        if (bindings.TryGetValue(module, out var bmap) && bmap.TryGetValue(name, out var bind))
        {
            var candidate = $"{bind.Module}:{bind.Original}";
            if (allFqns.Contains(candidate)) return candidate;
            // Imported symbol not found as a top-level def in target (re-export / type-only) → fall through.
        }

        // 2. Same-file local definition.
        if (localNames.TryGetValue(module, out var lmap) && lmap.TryGetValue(name, out var localFqn))
            return localFqn;

        // 3. Globally-unique top-level name (dynamic import(), barrels, imperfect export detection).
        if (globalNames.TryGetValue(name, out var gset) && gset.Count == 1)
            return gset.First();

        return null;
    }

    /// <summary>Resolve an import specifier to a known module FQN, or null if external/unresolvable.</summary>
    private string? ResolveSpecifier(string spec, string callerFqn, HashSet<string> knownModules)
    {
        if (string.IsNullOrEmpty(spec)) return null;

        string? basePath = null;

        if (spec == "." || spec == ".." || spec.StartsWith("./", StringComparison.Ordinal) || spec.StartsWith("../", StringComparison.Ordinal))
        {
            basePath = NormalizePath(Combine(DirOf(ModuleOf(callerFqn)), spec));
        }
        else
        {
            foreach (var (prefix, target) in _aliases)
            {
                if (spec.StartsWith(prefix, StringComparison.Ordinal))
                {
                    basePath = NormalizePath(Combine(target, spec[prefix.Length..]));
                    break;
                }
            }
        }

        if (basePath is null) return null; // bare package: react, next/server, @prisma/client, …

        if (knownModules.Contains(basePath)) return basePath;
        foreach (var ext in ModuleExtensions)
            if (knownModules.Contains(basePath + ext)) return basePath + ext;
        foreach (var ext in ModuleExtensions)
            if (knownModules.Contains(basePath + "/index" + ext)) return basePath + "/index" + ext;
        return null;
    }

    // --- FQN helpers ---

    private static (string? Module, string? Member) SplitFqn(string fqn)
    {
        var idx = fqn.IndexOf(':');
        return idx <= 0 ? (null, null) : (fqn[..idx], fqn[(idx + 1)..]);
    }

    private static string ModuleOf(string fqn)
    {
        var idx = fqn.IndexOf(':');
        return idx < 0 ? fqn : fqn[..idx];
    }

    private static bool IsTsModule(string fqn)
    {
        var module = ModuleOf(fqn);
        foreach (var ext in ModuleExtensions)
            if (module.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static bool IsBareName(string toFqn) =>
        toFqn.Length > 0
        && !toFqn.Contains(':')   // already an FQN
        && !toFqn.Contains('.')   // member expression (obj.method) or dotted path
        && !toFqn.Contains('(')   // signature
        && !toFqn.Contains('/');  // path-ish

    // --- path helpers (repo-relative, forward-slash) ---

    private static string DirOf(string path)
    {
        var idx = path.LastIndexOf('/');
        return idx < 0 ? "" : path[..idx];
    }

    private static string Combine(string dir, string rel) =>
        dir.Length == 0 ? rel : rel.Length == 0 ? dir : dir + "/" + rel;

    private static string NormalizePath(string path)
    {
        var stack = new List<string>();
        foreach (var part in path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".") continue;
            if (part == "..")
            {
                if (stack.Count > 0 && stack[^1] != "..") stack.RemoveAt(stack.Count - 1);
                else stack.Add("..");
            }
            else stack.Add(part);
        }
        return string.Join('/', stack);
    }

    // --- tsconfig / jsconfig alias loading ---

    private static List<(string Prefix, string Target)> LoadTsConfigAliases(string repoRoot)
    {
        var list = new List<(string, string)>();
        foreach (var name in new[] { "tsconfig.json", "jsconfig.json" })
        {
            var file = Path.Combine(repoRoot, name);
            if (!File.Exists(file)) continue;

            string text;
            try { text = StripJsonComments(File.ReadAllText(file)); }
            catch { continue; }

            var baseUrl = ".";
            var bm = Regex.Match(text, "\"baseUrl\"\\s*:\\s*\"([^\"]*)\"");
            if (bm.Success && !string.IsNullOrWhiteSpace(bm.Groups[1].Value)) baseUrl = bm.Groups[1].Value;
            var baseDir = NormalizePath(baseUrl);

            var pathsBlock = ExtractPathsBlock(text);
            if (pathsBlock is not null)
            {
                foreach (Match m in Regex.Matches(pathsBlock, "\"([^\"]+)\"\\s*:\\s*\\[\\s*\"([^\"]+)\""))
                {
                    var key = m.Groups[1].Value;     // "@/*"
                    var target = m.Groups[2].Value;  // "./src/*"
                    if (!key.EndsWith("*", StringComparison.Ordinal)) continue; // only wildcard prefixes
                    var keyPrefix = key[..^1];        // "@/"
                    var targetNoStar = target.EndsWith("*", StringComparison.Ordinal) ? target[..^1] : target;
                    var resolvedTarget = NormalizePath(Combine(baseDir, targetNoStar)); // "src"
                    list.Add((keyPrefix, resolvedTarget));
                }
            }
            if (list.Count > 0) break; // first config that yields aliases wins
        }
        return list;
    }

    /// <summary>Extract the balanced { … } block that follows a "paths" key.</summary>
    private static string? ExtractPathsBlock(string text)
    {
        var m = Regex.Match(text, "\"paths\"\\s*:\\s*\\{");
        if (!m.Success) return null;
        var start = m.Index + m.Length - 1; // at the opening '{'
        var depth = 0;
        for (var i = start; i < text.Length; i++)
        {
            if (text[i] == '{') depth++;
            else if (text[i] == '}' && --depth == 0)
                return text.Substring(start, i - start + 1);
        }
        return null;
    }

    /// <summary>
    /// Strip <c>//</c> line and <c>/* */</c> block comments from JSONC (tsconfig allows them),
    /// while preserving string literals. String-awareness is essential: tsconfig path values
    /// like <c>"./src/*"</c> and glob includes like <c>"**/*.ts"</c> contain literal <c>/*</c>
    /// and <c>*/</c> sequences that a regex stripper would treat as a comment span, deleting the
    /// whole <c>paths</c> block in between.
    /// </summary>
    private static string StripJsonComments(string text)
    {
        var sb = new System.Text.StringBuilder(text.Length);
        bool inString = false, inLine = false, inBlock = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            var next = i + 1 < text.Length ? text[i + 1] : '\0';

            if (inLine)
            {
                if (c is '\n' or '\r') { inLine = false; sb.Append(c); }
                continue;
            }
            if (inBlock)
            {
                if (c == '*' && next == '/') { inBlock = false; i++; }
                continue;
            }
            if (inString)
            {
                sb.Append(c);
                if (c == '\\' && i + 1 < text.Length) sb.Append(text[++i]); // keep escaped char verbatim
                else if (c == '"') inString = false;
                continue;
            }

            if (c == '"') { inString = true; sb.Append(c); }
            else if (c == '/' && next == '/') { inLine = true; i++; }
            else if (c == '/' && next == '*') { inBlock = true; i++; }
            else sb.Append(c);
        }
        return sb.ToString();
    }
}
