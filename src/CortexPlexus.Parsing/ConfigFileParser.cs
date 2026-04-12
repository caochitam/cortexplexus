using System.Text.Json;
using System.Text.RegularExpressions;
using CortexPlexus.Core.Models;

namespace CortexPlexus.Parsing;

/// <summary>
/// Parses configuration files and extracts config keys as CodeSymbol nodes.
/// Supported: appsettings*.json, .env, docker-compose.yml/yaml.
/// </summary>
internal static partial class ConfigFileParser
{
    private static readonly HashSet<string> AppSettingsPatterns = new(StringComparer.OrdinalIgnoreCase)
    {
        "appsettings.json",
        "appsettings.development.json",
        "appsettings.production.json",
        "appsettings.staging.json",
    };

    private static readonly string[] ExcludedDirectories =
    {
        "bin", "obj", "node_modules", ".git", ".vs", ".idea",
        "dist", "build", "target", "out", "publish", "packages"
    };

    /// <summary>
    /// Scans a directory recursively for known config files and extracts config key
    /// symbols. R20 fix: previously only scanned the top-level directory, missing
    /// config files in sub-projects (e.g. multi-project .NET solutions where
    /// appsettings.json lives in each project subfolder).
    /// </summary>
    public static List<CodeSymbol> ParseConfigFiles(string directoryPath)
    {
        var symbols = new List<CodeSymbol>();

        if (!Directory.Exists(directoryPath))
            return symbols;

        ScanDirectoryRecursive(directoryPath, directoryPath, symbols);

        return symbols;
    }

    private static void ScanDirectoryRecursive(string currentDir, string basePath, List<CodeSymbol> symbols)
    {
        // appsettings*.json in this directory
        foreach (var file in EnumerateFilesSafe(currentDir, "appsettings*.json"))
        {
            symbols.AddRange(ParseAppSettingsJson(file, basePath));
        }

        // .env files in this directory
        foreach (var file in EnumerateFilesSafe(currentDir, ".env*"))
        {
            var fileName = Path.GetFileName(file);
            if (fileName.StartsWith(".env", StringComparison.OrdinalIgnoreCase))
                symbols.AddRange(ParseDotEnv(file, basePath));
        }

        // docker-compose.yml / docker-compose.yaml in this directory
        foreach (var file in EnumerateFilesSafe(currentDir, "docker-compose*"))
        {
            var ext = Path.GetExtension(file);
            if (ext is ".yml" or ".yaml")
                symbols.AddRange(ParseDockerComposeEnv(file, basePath));
        }

        // Recurse into subdirectories, skipping build artifact / VCS / IDE dirs.
        IEnumerable<string> subdirs;
        try
        {
            subdirs = Directory.EnumerateDirectories(currentDir);
        }
        catch (UnauthorizedAccessException) { return; }
        catch (IOException) { return; }

        foreach (var subdir in subdirs)
        {
            var name = Path.GetFileName(subdir);
            if (string.IsNullOrEmpty(name)) continue;
            if (IsExcludedDir(name)) continue;

            ScanDirectoryRecursive(subdir, basePath, symbols);
        }
    }

    private static bool IsExcludedDir(string name)
    {
        foreach (var excluded in ExcludedDirectories)
        {
            if (string.Equals(name, excluded, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static IEnumerable<string> EnumerateFilesSafe(string dir, string pattern)
    {
        try { return Directory.EnumerateFiles(dir, pattern); }
        catch (UnauthorizedAccessException) { return []; }
        catch (IOException) { return []; }
    }

    /// <summary>
    /// Parses appsettings.json and flattens JSON keys to dotted paths.
    /// E.g., {"ConnectionStrings":{"Default":"..."}} → config:ConnectionStrings:Default
    /// </summary>
    public static List<CodeSymbol> ParseAppSettingsJson(string filePath, string basePath)
    {
        var symbols = new List<CodeSymbol>();
        try
        {
            var json = File.ReadAllText(filePath);
            var doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
            var relativePath = Path.GetRelativePath(basePath, filePath).Replace('\\', '/');

            FlattenJson(doc.RootElement, "", relativePath, symbols);
        }
        catch
        {
            // Skip malformed JSON
        }
        return symbols;
    }

    /// <summary>
    /// Parses .env files (KEY=VALUE format).
    /// </summary>
    public static List<CodeSymbol> ParseDotEnv(string filePath, string basePath)
    {
        var symbols = new List<CodeSymbol>();
        try
        {
            var relativePath = Path.GetRelativePath(basePath, filePath).Replace('\\', '/');
            var lines = File.ReadAllLines(filePath);
            var lineNum = 0;

            foreach (var line in lines)
            {
                lineNum++;
                var trimmed = line.Trim();
                if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith('#'))
                    continue;

                var eqIndex = trimmed.IndexOf('=');
                if (eqIndex <= 0) continue;

                var key = trimmed[..eqIndex].Trim();
                if (!IsValidEnvKey(key)) continue;

                symbols.Add(new ConfigKeyInfo
                {
                    Fqn = $"env:{key}",
                    Name = key,
                    Kind = "config_key",
                    FilePath = relativePath,
                    StartLine = lineNum,
                    Provider = "env",
                });
            }
        }
        catch
        {
            // Skip unreadable files
        }
        return symbols;
    }

    /// <summary>
    /// Parses docker-compose.yml environment section for env var definitions.
    /// Simple YAML parsing — extracts KEY=VALUE and KEY: VALUE from environment sections.
    /// </summary>
    public static List<CodeSymbol> ParseDockerComposeEnv(string filePath, string basePath)
    {
        var symbols = new List<CodeSymbol>();
        try
        {
            var relativePath = Path.GetRelativePath(basePath, filePath).Replace('\\', '/');
            var lines = File.ReadAllLines(filePath);
            var inEnvironment = false;
            var envIndent = 0;
            var lineNum = 0;

            foreach (var line in lines)
            {
                lineNum++;
                if (string.IsNullOrWhiteSpace(line)) continue;

                var indent = line.Length - line.TrimStart().Length;
                var trimmed = line.Trim();

                // Detect "environment:" section
                if (trimmed.StartsWith("environment:", StringComparison.OrdinalIgnoreCase))
                {
                    inEnvironment = true;
                    envIndent = indent;
                    continue;
                }

                if (inEnvironment)
                {
                    // Exit environment section when indent returns to same or lower level
                    if (!trimmed.StartsWith('-') && !trimmed.StartsWith('#') && indent <= envIndent && !string.IsNullOrWhiteSpace(trimmed))
                    {
                        inEnvironment = false;
                        continue;
                    }

                    // Parse "- KEY=VALUE" or "KEY: VALUE"
                    var entry = trimmed.TrimStart('-').Trim();
                    if (string.IsNullOrEmpty(entry) || entry.StartsWith('#')) continue;

                    string? key = null;
                    if (entry.Contains('='))
                    {
                        key = entry[..entry.IndexOf('=')].Trim();
                    }
                    else if (entry.Contains(':'))
                    {
                        key = entry[..entry.IndexOf(':')].Trim();
                    }

                    if (key is not null && IsValidEnvKey(key))
                    {
                        symbols.Add(new ConfigKeyInfo
                        {
                            Fqn = $"env:{key}",
                            Name = key,
                            Kind = "config_key",
                            FilePath = relativePath,
                            StartLine = lineNum,
                            Provider = "docker-compose",
                        });
                    }
                }
            }
        }
        catch
        {
            // Skip unreadable files
        }
        return symbols;
    }

    private static void FlattenJson(JsonElement element, string prefix, string filePath, List<CodeSymbol> symbols)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    var key = string.IsNullOrEmpty(prefix) ? property.Name : $"{prefix}:{property.Name}";
                    FlattenJson(property.Value, key, filePath, symbols);
                }
                break;

            case JsonValueKind.Array:
                // Skip arrays — individual items aren't meaningful config keys
                break;

            default:
                // Leaf value — this is a concrete config key
                if (!string.IsNullOrEmpty(prefix))
                {
                    symbols.Add(new ConfigKeyInfo
                    {
                        Fqn = $"config:{prefix}",
                        Name = prefix,
                        Kind = "config_key",
                        FilePath = filePath,
                        StartLine = null,
                        Provider = "appsettings",
                    });
                }
                break;
        }
    }

    private static bool IsValidEnvKey(string key)
    {
        // Env var keys: letters, digits, underscores. Must start with letter or underscore.
        if (string.IsNullOrEmpty(key)) return false;
        if (!char.IsLetter(key[0]) && key[0] != '_') return false;
        foreach (var c in key)
        {
            if (!char.IsLetterOrDigit(c) && c != '_') return false;
        }
        return true;
    }
}
