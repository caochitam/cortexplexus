using System.Text.RegularExpressions;

namespace CortexPlexus.Parsing.TreeSitter;

/// <summary>
/// Shared helper for extracting documentation comments from tree-sitter AST nodes.
/// Supports JSDoc (/** */), Python docstrings (""" """), Javadoc, Go/Rust doc comments.
/// </summary>
internal static partial class DocCommentHelper
{
    /// <summary>
    /// Finds the documentation comment immediately preceding a declaration node.
    /// Works for JSDoc, Javadoc, PHPDoc (/** ... */), Go (// ...), Rust (/// ...).
    /// </summary>
    public static string? GetPrecedingDocComment(global::TreeSitter.Node node)
    {
        var parent = node.Parent;
        if (parent is null) return null;

        // Walk siblings before this node, find last comment
        global::TreeSitter.Node? lastComment = null;
        foreach (var sibling in parent.Children)
        {
            if (sibling.Equals(node)) break;
            if (sibling.Type == "comment")
                lastComment = sibling;
            else if (sibling.IsNamed)
                lastComment = null; // Reset if there's a non-comment named node between
        }

        if (lastComment is null) return null;

        var text = lastComment.Text ?? "";
        return CleanComment(text);
    }

    /// <summary>
    /// Extracts Python docstring from function/class body.
    /// Python docstrings are the first expression_statement containing a string in the body.
    /// </summary>
    public static string? GetPythonDocstring(global::TreeSitter.Node bodyNode)
    {
        foreach (var child in bodyNode.Children)
        {
            if (child.Type == "expression_statement")
            {
                foreach (var inner in child.Children)
                {
                    if (inner.Type == "string")
                    {
                        var text = inner.Text ?? "";
                        return CleanDocstring(text);
                    }
                }
                break; // Only check first expression_statement
            }
            if (child.IsNamed && child.Type != "comment")
                break; // Non-string statement found first → no docstring
        }
        return null;
    }

    /// <summary>
    /// Extracts Rust doc comments (/// or //!) that appear as consecutive comment nodes.
    /// </summary>
    public static string? GetRustDocComment(global::TreeSitter.Node node)
    {
        var parent = node.Parent;
        if (parent is null) return null;

        // Collect consecutive line_comment nodes ending with "///" before this node
        var docLines = new List<string>();
        var foundTarget = false;

        foreach (var sibling in parent.Children)
        {
            if (sibling.Equals(node))
            {
                foundTarget = true;
                break;
            }

            if (sibling.Type == "line_comment")
            {
                var text = sibling.Text ?? "";
                if (text.StartsWith("///", StringComparison.Ordinal) || text.StartsWith("//!", StringComparison.Ordinal))
                {
                    var line = text.Length > 3 ? text[3..].Trim() : "";
                    docLines.Add(line);
                }
                else
                {
                    docLines.Clear(); // Reset on non-doc comment
                }
            }
            else if (sibling.IsNamed)
            {
                docLines.Clear();
            }
        }

        if (!foundTarget || docLines.Count == 0) return null;
        return string.Join(" ", docLines).Trim();
    }

    /// <summary>
    /// Clean block comment (/** ... */, /* ... */) → plain text.
    /// </summary>
    private static string CleanComment(string comment)
    {
        // Remove /** */ or /* */ markers
        if (comment.StartsWith("/**", StringComparison.Ordinal))
            comment = comment[3..];
        else if (comment.StartsWith("/*", StringComparison.Ordinal))
            comment = comment[2..];

        if (comment.EndsWith("*/", StringComparison.Ordinal))
            comment = comment[..^2];

        // Remove leading * on each line (Javadoc/JSDoc style)
        comment = LeadingStarRegex().Replace(comment, "");

        // Remove @param, @returns, @throws lines (keep description only)
        var lines = comment.Split('\n')
            .Select(l => l.Trim())
            .Where(l => !l.StartsWith('@'))
            .Where(l => !string.IsNullOrWhiteSpace(l));

        var result = string.Join(" ", lines).Trim();
        return string.IsNullOrWhiteSpace(result) ? null! : result;
    }

    /// <summary>
    /// Clean Python docstring (""" ... """ or ''' ... ''') → plain text.
    /// </summary>
    private static string CleanDocstring(string docstring)
    {
        // Strip triple quotes
        if (docstring.StartsWith("\"\"\"", StringComparison.Ordinal))
            docstring = docstring[3..];
        else if (docstring.StartsWith("'''", StringComparison.Ordinal))
            docstring = docstring[3..];
        else if (docstring.StartsWith("\"", StringComparison.Ordinal) || docstring.StartsWith("'", StringComparison.Ordinal))
            docstring = docstring[1..];

        if (docstring.EndsWith("\"\"\"", StringComparison.Ordinal))
            docstring = docstring[..^3];
        else if (docstring.EndsWith("'''", StringComparison.Ordinal))
            docstring = docstring[..^3];
        else if (docstring.EndsWith("\"", StringComparison.Ordinal) || docstring.EndsWith("'", StringComparison.Ordinal))
            docstring = docstring[..^1];

        // Remove leading whitespace per line, keep first paragraph
        var lines = docstring.Split('\n')
            .Select(l => l.Trim())
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .TakeWhile(l => !l.StartsWith(':') && !l.StartsWith("Args:") && !l.StartsWith("Returns:") && !l.StartsWith("Raises:"));

        var result = string.Join(" ", lines).Trim();
        return string.IsNullOrWhiteSpace(result) ? null! : result;
    }

    [GeneratedRegex(@"^\s*\*\s?", RegexOptions.Multiline)]
    private static partial Regex LeadingStarRegex();
}
