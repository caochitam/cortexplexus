using CortexPlexus.Core.Models;

namespace CortexPlexus.Core.Abstractions;

public interface ICodeParser
{
    Task<ParseResult> ParseSolutionAsync(string solutionPath, CancellationToken ct = default);
    Task<ParseResult> ParseFilesAsync(IEnumerable<string> filePaths, string projectPath, CancellationToken ct = default);
}
