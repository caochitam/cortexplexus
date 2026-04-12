using CortexPlexus.Core.Models;
using CortexPlexus.Parsing.Markdown;
using Microsoft.Extensions.Logging.Abstractions;

namespace CortexPlexus.Parsing.Tests;

public sealed class MarkdownParserTests : IDisposable
{
    private readonly MarkdownParser _parser = new(NullLogger<MarkdownParser>.Instance);
    private readonly string _tempDir;

    public MarkdownParserTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"cortexplexus-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public void ParseDirectory_ExtractsDocumentSymbol()
    {
        WriteFile("README.md", "# My Project\n\nSome description.");

        var result = _parser.ParseDirectory(_tempDir);

        Assert.True(result.Symbols.Count > 0);
        var doc = result.Symbols.OfType<DocumentSection>().First(s => s.Kind == "document");
        Assert.Equal("README", doc.Name);
        Assert.Equal("doc:README.md", doc.Fqn);
        Assert.Equal(0, doc.Level);
    }

    [Fact]
    public void ParseDirectory_ExtractsSections()
    {
        WriteFile("guide.md", """
            # Getting Started

            Install the dependencies.

            ## Prerequisites

            You need Docker.

            ## Installation

            Run docker compose up.
            """);

        var result = _parser.ParseDirectory(_tempDir);
        var sections = result.Symbols.OfType<DocumentSection>().Where(s => s.Kind == "section").ToList();

        Assert.Equal(3, sections.Count);
        Assert.Contains(sections, s => s.Name == "Getting Started" && s.Level == 1);
        Assert.Contains(sections, s => s.Name == "Prerequisites" && s.Level == 2);
        Assert.Contains(sections, s => s.Name == "Installation" && s.Level == 2);
    }

    [Fact]
    public void ParseDirectory_SectionContentIsExtracted()
    {
        WriteFile("doc.md", "# Setup\n\nRun `docker compose up -d` to start.");

        var result = _parser.ParseDirectory(_tempDir);
        var section = result.Symbols.OfType<DocumentSection>().First(s => s.Name == "Setup");

        Assert.Contains("docker compose up", section.Content);
    }

    [Fact]
    public void ParseDirectory_GeneratesFqnWithSlug()
    {
        WriteFile("doc.md", "# Query Flow\n\nDescription here.");

        var result = _parser.ParseDirectory(_tempDir);
        var section = result.Symbols.OfType<DocumentSection>().First(s => s.Name == "Query Flow");

        Assert.Equal("doc:doc.md#query-flow", section.Fqn);
    }

    [Fact]
    public void ParseDirectory_CreatesHasSectionRelationships()
    {
        WriteFile("arch.md", "# Overview\n\nText.\n\n## Components\n\nMore text.");

        var result = _parser.ParseDirectory(_tempDir);

        Assert.True(result.Relationships.Count >= 2);
        Assert.All(result.Relationships, r => Assert.Equal(RelationshipType.HasSection, r.Type));
        Assert.All(result.Relationships, r => Assert.Equal("doc:arch.md", r.FromFqn));
    }

    [Fact]
    public void ParseDirectory_IgnoresExcludedDirs()
    {
        Directory.CreateDirectory(Path.Combine(_tempDir, "node_modules"));
        WriteFile("node_modules/package.md", "# Should be ignored");
        WriteFile("real.md", "# Real Doc\n\nContent.");

        var result = _parser.ParseDirectory(_tempDir);

        Assert.DoesNotContain(result.Symbols, s => s.Fqn.Contains("node_modules"));
    }

    [Fact]
    public void ParseDirectory_HandlesNestedDirectories()
    {
        Directory.CreateDirectory(Path.Combine(_tempDir, "docs"));
        WriteFile("docs/setup.md", "# Setup Guide\n\nStep by step.");

        var result = _parser.ParseDirectory(_tempDir);
        var doc = result.Symbols.OfType<DocumentSection>().First(s => s.Kind == "document");

        Assert.Equal("doc:docs/setup.md", doc.Fqn);
    }

    [Fact]
    public void ParseDirectory_EmptyDirectory_ReturnsEmpty()
    {
        var result = _parser.ParseDirectory(_tempDir);

        Assert.Empty(result.Symbols);
        Assert.Equal(0, result.FilesProcessed);
    }

    [Fact]
    public void ParseDirectory_NoMarkdownFiles_ReturnsEmpty()
    {
        WriteFile("code.cs", "public class Foo {}");

        var result = _parser.ParseDirectory(_tempDir);

        Assert.Empty(result.Symbols);
    }

    [Fact]
    public void ParseFile_TruncatesLongContent()
    {
        var longContent = "# Title\n\n" + new string('x', 1000);
        WriteFile("long.md", longContent);

        var result = _parser.ParseDirectory(_tempDir);
        var section = result.Symbols.OfType<DocumentSection>().First(s => s.Name == "Title");

        Assert.True(section.Content.Length <= 503); // 500 + "..."
        Assert.EndsWith("...", section.Content);
    }

    [Fact]
    public void ParseFile_SetsCorrectLineNumbers()
    {
        WriteFile("lines.md", "# First\n\nContent line 1.\nContent line 2.\n\n# Second\n\nMore content.");

        var result = _parser.ParseDirectory(_tempDir);
        var sections = result.Symbols.OfType<DocumentSection>().Where(s => s.Kind == "section").ToList();

        var first = sections.First(s => s.Name == "First");
        Assert.Equal(1, first.StartLine);

        var second = sections.First(s => s.Name == "Second");
        Assert.Equal(6, second.StartLine);
    }

    private void WriteFile(string relativePath, string content)
    {
        var fullPath = Path.Combine(_tempDir, relativePath);
        var dir = Path.GetDirectoryName(fullPath)!;
        Directory.CreateDirectory(dir);
        File.WriteAllText(fullPath, content);
    }
}
