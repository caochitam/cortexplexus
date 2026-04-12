using System.ComponentModel;
using ModelContextProtocol.Server;

namespace CortexPlexus.App.Mcp.Tools;

[McpServerToolType]
public sealed class AgentTools
{
    [McpServerTool, Description(
        "Activate CortexPlexus local indexing agent for a project. " +
        "The agent runs on your machine, parses source code locally, and sends only metadata (no source code) to the server. " +
        "Call this at session start to auto-index the current project. " +
        "Returns install/start commands for the AI to execute locally via Bash. " +
        "IMPORTANT: AI must execute the returned commands step-by-step using Bash tool.")]
    public static string ActivateAgent(
        [Description("Absolute path to the project directory on your local machine")] string projectPath,
        [Description("Project name (used as identifier in CortexPlexus)")] string? projectName = null,
        [Description("Override server URL (default: auto-detect from MCP connection)")] string? serverUrl = null)
    {
        if (string.IsNullOrWhiteSpace(projectPath))
            return "Error: projectPath is required.";

        var name = projectName ?? Path.GetFileName(projectPath.TrimEnd('/', '\\'));
        // Default to localhost; deployments running on a remote host should pass
        // serverUrl explicitly or set the CORTEXPLEXUS_SERVER_URL env var.
        var server = serverUrl
                  ?? Environment.GetEnvironmentVariable("CORTEXPLEXUS_SERVER_URL")
                  ?? "http://localhost:8080";

        // Detect client platform from project path
        var isWindows = projectPath.Contains('\\') || projectPath.Contains(':');
        var installDir = isWindows
            ? "$USERPROFILE/.cortexplexus/agent"
            : "$HOME/.cortexplexus/agent";
        var agentDll = $"{installDir}/cortexplexus-agent.dll";

        return $"""
            # CortexPlexus Agent — Execute these commands via Bash tool

            ## Prerequisites
            - .NET SDK must be installed (agent is framework-dependent)
            - Verify: `dotnet --version`

            ## Step 1: Check if agent is already running
            ```bash
            {(isWindows
                ? "tasklist /FI \"IMAGENAME eq dotnet.exe\" /FO CSV 2>/dev/null | grep -i cortexplexus-agent || echo NOT_RUNNING"
                : "pgrep -f cortexplexus-agent || echo NOT_RUNNING")}
            ```
            If running → skip to Step 4.

            ## Step 2: Install agent (first time only)
            ```bash
            test -f "{agentDll}" && echo "INSTALLED" || echo "NOT_INSTALLED"
            ```
            If NOT_INSTALLED:
            ```bash
            mkdir -p "{installDir}"
            curl -sL "{server}/api/agent/download?platform={(isWindows ? "win-x64" : "linux-x64")}" -o /tmp/cortexplexus-agent.tar.gz
            tar -xzf /tmp/cortexplexus-agent.tar.gz -C "{installDir}"
            rm /tmp/cortexplexus-agent.tar.gz
            ```

            ## Step 3: Start agent (one-time index first, then watch)
            ```bash
            dotnet "{agentDll}" index "{projectPath}" --server {server} --name "{name}"
            ```
            After index succeeds, start watch mode in background:
            ```bash
            nohup dotnet "{agentDll}" watch "{projectPath}" --server {server} --name "{name}" > /dev/null 2>&1 &
            ```

            ## Step 4: Verify
            Call `ListRepositories()` — project "{name}" should appear.
            Then use `SearchCode` or `ExploreTopic` to query.

            ## Commands reference
            ```
            dotnet {agentDll} watch <path> --server <url> --name <name>   # Watch mode
            dotnet {agentDll} index <path> --server <url> --name <name>   # One-time index
            dotnet {agentDll} status                                       # Show running
            dotnet {agentDll} stop --all                                   # Stop all
            ```
            """;
    }
}
