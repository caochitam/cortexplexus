namespace CortexPlexus.Parsing.TreeSitter;

/// <summary>
/// Maps file extensions to tree-sitter languages and language kind strings.
/// </summary>
internal sealed class LanguageRegistry
{
    private static readonly Dictionary<string, string> LanguageIds = new(StringComparer.OrdinalIgnoreCase)
    {
        [".ts"] = "typescript",
        [".tsx"] = "tsx",
        [".js"] = "javascript",
        [".jsx"] = "javascript",
        [".py"] = "python",
        [".java"] = "java",
        [".go"] = "go",
        [".rs"] = "rust",
        [".php"] = "php",
    };

    private static readonly Dictionary<string, string> LanguageKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        [".ts"] = "typescript",
        [".tsx"] = "typescript",
        [".js"] = "javascript",
        [".jsx"] = "javascript",
        [".py"] = "python",
        [".java"] = "java",
        [".go"] = "go",
        [".rs"] = "rust",
        [".php"] = "php",
    };

    /// <summary>
    /// Returns the tree-sitter Language for the given file path,
    /// or <c>null</c> if the file extension is not supported.
    /// </summary>
    public global::TreeSitter.Language? GetLanguage(string filePath)
    {
        var ext = Path.GetExtension(filePath);
        if (LanguageIds.TryGetValue(ext, out var id))
        {
            return new global::TreeSitter.Language(id);
        }
        return null;
    }

    /// <summary>
    /// Returns "typescript", "javascript", or "python" for the given file path,
    /// or <c>null</c> if the extension is not supported.
    /// </summary>
    public string? GetLanguageKind(string filePath)
    {
        var ext = Path.GetExtension(filePath);
        return LanguageKinds.GetValueOrDefault(ext);
    }
}
