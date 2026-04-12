using System.Xml.Linq;
using CortexPlexus.Core.Models;

namespace CortexPlexus.Parsing;

public sealed class NuGetAuditAnalyzer
{
    public IReadOnlyList<NuGetPackageInfo> AnalyzeProject(string csprojPath)
    {
        if (!File.Exists(csprojPath))
            return [];

        var doc = XDocument.Load(csprojPath);
        var projectName = Path.GetFileNameWithoutExtension(csprojPath);

        return doc.Descendants("PackageReference")
            .Select(pr => new NuGetPackageInfo(
                PackageId: pr.Attribute("Include")?.Value ?? "unknown",
                Version: pr.Attribute("Version")?.Value ?? pr.Element("Version")?.Value ?? "*",
                ProjectName: projectName
            ))
            .ToList();
    }

    public IReadOnlyList<NuGetPackageInfo> AnalyzeDirectory(string directoryPath)
    {
        // Agent-uploaded projects (e.g. /workspace/_agent/CortexFlow) do not have
        // source on the server — only metadata in the graph. Return empty for
        // missing directories instead of throwing DirectoryNotFoundException.
        if (string.IsNullOrWhiteSpace(directoryPath) || !Directory.Exists(directoryPath))
            return [];

        var results = new List<NuGetPackageInfo>();

        var csprojFiles = Directory.GetFiles(directoryPath, "*.csproj", SearchOption.AllDirectories);
        foreach (var csproj in csprojFiles)
        {
            results.AddRange(AnalyzeProject(csproj));
        }

        return results;
    }
}
