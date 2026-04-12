using System.Diagnostics;
using CortexPlexus.Core.Abstractions;
using CortexPlexus.Core.Models;
using Microsoft.Extensions.Logging;

namespace CortexPlexus.Parsing.TreeSitter;

/// <summary>
/// <see cref="ICodeParser"/> implementation that uses tree-sitter grammars to parse
/// TypeScript, JavaScript, TSX, and Python source files.
/// </summary>
public sealed class TreeSitterCodeParser : ICodeParser
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".ts", ".tsx", ".js", ".jsx", ".py",
        ".java", ".go", ".rs", ".php"
    };

    private readonly LanguageRegistry _registry = new();
    private readonly ILogger<TreeSitterCodeParser> _logger;

    public TreeSitterCodeParser(ILogger<TreeSitterCodeParser> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public Task<ParseResult> ParseSolutionAsync(string path, CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var allSymbols = new List<CodeSymbol>();
        var allRelationships = new List<Relationship>();
        var filesProcessed = 0;
        var errorCount = 0;

        if (!Directory.Exists(path))
        {
            _logger.LogWarning("Directory does not exist: {Path}", path);
            stopwatch.Stop();
            return Task.FromResult(new ParseResult(allSymbols, allRelationships, stopwatch.Elapsed, 0, 1));
        }

        _logger.LogInformation("Scanning directory for supported files: {Path}", path);

        // Load .cortexplexusignore từ root directory (nếu có) — gitignore-style patterns.
        var ignorePatterns = IgnorePatternMatcher.LoadFromDirectory(path);
        if (ignorePatterns.Count > 0)
            _logger.LogInformation("Loaded {Count} ignore patterns from .cortexplexusignore", ignorePatterns.Count);

        var files = Directory.EnumerateFiles(path, "*.*", SearchOption.AllDirectories)
            .Where(f => SupportedExtensions.Contains(Path.GetExtension(f)))
            .Where(f => !IsInExcludedDirectory(f))
            .Where(f => !IgnorePatternMatcher.Matches(f, path, ignorePatterns))
            .ToList();

        _logger.LogInformation("Found {FileCount} supported files", files.Count);

        // P1b: Parse config files in directory
        allSymbols.AddRange(ConfigFileParser.ParseConfigFiles(path));

        foreach (var filePath in files)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var (symbols, relationships) = ParseSingleFile(filePath, path);
                allSymbols.AddRange(symbols);
                allRelationships.AddRange(relationships);
                filesProcessed++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Error parsing file: {FilePath}", filePath);
                errorCount++;
            }
        }

        stopwatch.Stop();

        _logger.LogInformation(
            "Parsed {Files} files in {Duration}ms — {Symbols} symbols, {Relationships} relationships, {Errors} errors",
            filesProcessed, stopwatch.ElapsedMilliseconds, allSymbols.Count, allRelationships.Count, errorCount);

        return Task.FromResult(new ParseResult(allSymbols, allRelationships, stopwatch.Elapsed, filesProcessed, errorCount));
    }

    /// <inheritdoc />
    public Task<ParseResult> ParseFilesAsync(IEnumerable<string> filePaths, string projectPath, CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var allSymbols = new List<CodeSymbol>();
        var allRelationships = new List<Relationship>();
        var filesProcessed = 0;
        var errorCount = 0;

        foreach (var filePath in filePaths)
        {
            ct.ThrowIfCancellationRequested();

            if (!SupportedExtensions.Contains(Path.GetExtension(filePath)))
            {
                _logger.LogDebug("Skipping unsupported file: {FilePath}", filePath);
                continue;
            }

            try
            {
                var (symbols, relationships) = ParseSingleFile(filePath, projectPath);
                allSymbols.AddRange(symbols);
                allRelationships.AddRange(relationships);
                filesProcessed++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Error parsing file: {FilePath}", filePath);
                errorCount++;
            }
        }

        stopwatch.Stop();

        _logger.LogInformation(
            "Parsed {Files} files in {Duration}ms — {Symbols} symbols, {Relationships} relationships, {Errors} errors",
            filesProcessed, stopwatch.ElapsedMilliseconds, allSymbols.Count, allRelationships.Count, errorCount);

        return Task.FromResult(new ParseResult(allSymbols, allRelationships, stopwatch.Elapsed, filesProcessed, errorCount));
    }

    private (List<CodeSymbol> Symbols, List<Relationship> Relationships) ParseSingleFile(
        string filePath, string basePath)
    {
        var language = _registry.GetLanguage(filePath);
        var languageKind = _registry.GetLanguageKind(filePath);

        if (language is null || languageKind is null)
            return ([], []);

        var sourceCode = File.ReadAllText(filePath);
        var relativePath = Path.GetRelativePath(basePath, filePath).Replace('\\', '/');

        using var parser = new global::TreeSitter.Parser(language);
        using var tree = parser.Parse(sourceCode);
        if (tree is null)
            return ([], []);

        var root = tree.RootNode;

        return languageKind switch
        {
            "typescript" or "javascript" => new TypeScriptExtractor(sourceCode, filePath, relativePath).Extract(root),
            "python" => new PythonExtractor(sourceCode, filePath, relativePath).Extract(root),
            "java" => new JavaExtractor(sourceCode, filePath, relativePath).Extract(root),
            "go" => new GoExtractor(sourceCode, filePath, relativePath).Extract(root),
            "rust" => new RustExtractor(sourceCode, filePath, relativePath).Extract(root),
            "php" => new PhpExtractor(sourceCode, filePath, relativePath).Extract(root),
            _ => ([], []),
        };
    }

    private static bool IsInExcludedDirectory(string filePath)
    {
        var normalized = filePath.Replace('\\', '/');
        return normalized.Contains("/node_modules/")
            || normalized.Contains("/.git/")
            || normalized.Contains("/dist/")
            || normalized.Contains("/build/")
            || normalized.Contains("/bin/")        // .NET build output
            || normalized.Contains("/obj/")        // .NET intermediate output
            || normalized.Contains("/__pycache__/")
            || normalized.Contains("/.venv/")
            || normalized.Contains("/venv/")
            || normalized.Contains("/target/")     // Java Maven + Rust Cargo
            || normalized.Contains("/.gradle/")    // Java Gradle
            || normalized.Contains("/vendor/");    // Go + PHP
    }
}
