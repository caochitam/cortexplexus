namespace CortexPlexus.Core.Models;

/// <summary>
/// Represents a section extracted from a Markdown document.
/// FQN format: "doc:{relative_path}#{heading}" (e.g., "doc:docs/ARCHITECTURE.md#Query Flow")
/// </summary>
public sealed record DocumentSection : CodeSymbol
{
    /// <summary>Heading level (1-6), or 0 for document-level.</summary>
    public int Level { get; init; }

    /// <summary>Plain text content of this section (under the heading, before the next heading).</summary>
    public required string Content { get; init; }

    /// <summary>Relative path of the source document within the project.</summary>
    public required string DocumentPath { get; init; }
}
