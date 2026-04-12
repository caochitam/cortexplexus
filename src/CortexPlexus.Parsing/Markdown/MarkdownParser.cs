using CortexPlexus.Core.Models;
using Microsoft.Extensions.Logging;

namespace CortexPlexus.Parsing.Markdown;

/// <summary>
/// Parses Markdown files into DocumentSection symbols.
/// Splits documents by headings — each heading becomes a searchable symbol with its content.
/// </summary>
public sealed class MarkdownParser(ILogger<MarkdownParser> logger)
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".md", ".markdown"
    };

    private static readonly HashSet<string> ExcludedDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", "bin", "obj", ".git", ".vs", "__pycache__", ".venv", "venv"
    };

    /// <summary>
    /// Scan a directory for Markdown files and parse them into DocumentSection symbols.
    /// </summary>
    public ParseResult ParseDirectory(string directoryPath)
    {
        var allSymbols = new List<CodeSymbol>();
        var allRelationships = new List<Relationship>();
        var fileCount = 0;

        // Honor .cortexplexusignore from root directory (consistent với TreeSitter parser)
        var ignorePatterns = IgnorePatternMatcher.LoadFromDirectory(directoryPath);

        var mdFiles = Directory.GetFiles(directoryPath, "*.*", SearchOption.AllDirectories)
            .Where(f => SupportedExtensions.Contains(Path.GetExtension(f)))
            .Where(f => !IsInExcludedDirectory(f))
            .Where(f => !IgnorePatternMatcher.Matches(f, directoryPath, ignorePatterns))
            .ToList();

        foreach (var file in mdFiles)
        {
            try
            {
                var relativePath = Path.GetRelativePath(directoryPath, file).Replace('\\', '/');
                var (sections, relationships) = ParseFile(file, relativePath);
                allSymbols.AddRange(sections);
                allRelationships.AddRange(relationships);
                fileCount++;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to parse Markdown file: {File}", file);
            }
        }

        if (fileCount > 0)
            logger.LogInformation("Markdown: {Sections} sections from {Files} files", allSymbols.Count, fileCount);

        return new ParseResult(allSymbols, allRelationships, TimeSpan.Zero, fileCount, 0);
    }

    /// <summary>
    /// Parse a single Markdown file into DocumentSection symbols.
    /// </summary>
    internal (List<DocumentSection> Sections, List<Relationship> Relationships) ParseFile(string filePath, string relativePath)
    {
        var content = File.ReadAllText(filePath);
        var lines = content.Split('\n');

        var sections = new List<DocumentSection>();
        var relationships = new List<Relationship>();

        // Document-level symbol (the file itself)
        var docFqn = $"doc:{relativePath}";
        var docName = Path.GetFileNameWithoutExtension(filePath);

        // Track current section state
        string? currentHeading = null;
        int currentLevel = 0;
        int currentStartLine = 1;
        var currentContent = new List<string>();

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.TrimStart();

            // Detect ATX heading (# Heading)
            if (trimmed.StartsWith('#'))
            {
                // Flush previous section
                if (currentHeading is not null || currentContent.Count > 0)
                {
                    var section = BuildSection(
                        docFqn, relativePath, filePath,
                        currentHeading ?? docName, currentLevel, currentStartLine,
                        i, // endLine = line before this heading
                        currentContent);

                    if (section is not null)
                    {
                        sections.Add(section);
                        relationships.Add(new Relationship(docFqn, section.Fqn, RelationshipType.HasSection));
                    }
                }

                // Parse new heading
                var level = 0;
                while (level < trimmed.Length && trimmed[level] == '#') level++;
                var headingText = trimmed[level..].Trim();

                currentHeading = headingText;
                currentLevel = level;
                currentStartLine = i + 1; // 1-based
                currentContent.Clear();
            }
            else
            {
                currentContent.Add(line);
            }
        }

        // Flush last section
        if (currentHeading is not null || currentContent.Count > 0)
        {
            var section = BuildSection(
                docFqn, relativePath, filePath,
                currentHeading ?? docName, currentLevel, currentStartLine,
                lines.Length, currentContent);

            if (section is not null)
            {
                sections.Add(section);
                relationships.Add(new Relationship(docFqn, section.Fqn, RelationshipType.HasSection));
            }
        }

        // Add document-level symbol if we found any sections
        if (sections.Count > 0)
        {
            // Build a summary from first ~200 chars of the file
            var summary = content.Length > 200 ? content[..200] + "..." : content;

            sections.Insert(0, new DocumentSection
            {
                Fqn = docFqn,
                Name = docName,
                Kind = "document",
                FilePath = filePath,
                StartLine = 1,
                EndLine = lines.Length,
                Level = 0,
                Content = summary,
                DocumentPath = relativePath
            });
        }

        return (sections, relationships);
    }

    private static DocumentSection? BuildSection(
        string docFqn, string relativePath, string filePath,
        string heading, int level, int startLine, int endLine,
        List<string> contentLines)
    {
        var text = string.Join('\n', contentLines).Trim();
        if (string.IsNullOrWhiteSpace(heading) && string.IsNullOrWhiteSpace(text))
            return null;

        // Truncate content for storage (keep first 500 chars for embedding)
        var truncated = text.Length > 500 ? text[..500] + "..." : text;

        var sectionFqn = $"{docFqn}#{Slugify(heading)}";

        return new DocumentSection
        {
            Fqn = sectionFqn,
            Name = heading,
            Kind = "section",
            FilePath = filePath,
            StartLine = startLine,
            EndLine = endLine,
            Level = level,
            Content = truncated,
            DocumentPath = relativePath
        };
    }

    private static string Slugify(string text)
    {
        return text
            .ToLowerInvariant()
            .Replace(' ', '-')
            .Replace("---", "-")
            .Replace("--", "-")
            .Trim('-');
    }

    private static bool IsInExcludedDirectory(string filePath)
    {
        var parts = filePath.Split(Path.DirectorySeparatorChar);
        return parts.Any(p => ExcludedDirs.Contains(p));
    }
}
