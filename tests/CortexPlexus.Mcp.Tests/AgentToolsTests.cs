using CortexPlexus.App.Mcp.Tools;

namespace CortexPlexus.Mcp.Tests;

/// <summary>
/// Tests for ActivateAgent output — verifies the recipe includes the pieces
/// AI agents rely on to self-execute end-to-end: server URL, prereqs, install,
/// watch, verify, and VS Code auto-start (Step 8, added v0.8.4).
/// </summary>
public sealed class AgentToolsTests
{
    [Fact]
    public async Task ActivateAgent_IncludesVsCodeAutoStartSnippet_Windows()
    {
        var result = await AgentTools.ActivateAgent(
            projectPath: @"C:\src\myproject",
            projectName: "myproject",
            serverUrl: "http://srv:8080");

        // Section anchor
        Assert.Contains("## Step 8", result);
        Assert.Contains("Auto-start on VS Code folder open", result);

        // Snippet fingerprints an AI can match against
        Assert.Contains("\"label\": \"CortexPlexus: start watch\"", result);
        Assert.Contains("\"runOn\": \"folderOpen\"", result);
        Assert.Contains("${workspaceFolder}", result);
        Assert.Contains("${workspaceFolderBasename}", result);

        // Platform-correct agent dll path
        Assert.Contains("${env:USERPROFILE}/.cortexplexus/agent/cortexplexus-agent.dll", result);

        // Explicit server URL from recipe baked into snippet
        Assert.Contains("\"--server\", \"http://srv:8080\"", result);

        // AI decision procedure is present (merge logic — preserve existing tasks)
        Assert.Contains("already contains", result);
        Assert.Contains("preserve every existing task", result);
    }

    [Fact]
    public async Task ActivateAgent_IncludesVsCodeAutoStartSnippet_Linux()
    {
        var result = await AgentTools.ActivateAgent(
            projectPath: "/home/alice/code/proj",
            projectName: "proj",
            serverUrl: "http://srv:8080");

        Assert.Contains("## Step 8", result);
        // Linux uses $HOME, not $USERPROFILE
        Assert.Contains("${env:HOME}/.cortexplexus/agent/cortexplexus-agent.dll", result);
        Assert.DoesNotContain("${env:USERPROFILE}", result);
    }

    [Fact]
    public async Task ActivateAgent_AutoStartSection_FlagsLimitationsHonestly()
    {
        // The user should never be promised reboot-survival from tasks.json alone;
        // Step 8 must point them at the runbook for OS-level supervisors.
        var result = await AgentTools.ActivateAgent(
            projectPath: "/tmp/x",
            projectName: "x",
            serverUrl: "http://s");

        Assert.Contains("Not reboot-survival", result);
        Assert.Contains("docs/runbooks/agent-auto-start.md", result);
        Assert.Contains("systemd", result); // pointer to other supervisors
    }

    [Fact]
    public async Task ActivateAgent_ReportsServerUrlSource_WhenExplicit()
    {
        var result = await AgentTools.ActivateAgent(
            projectPath: "/tmp/x",
            projectName: "x",
            serverUrl: "http://custom:9999");

        Assert.Contains("http://custom:9999", result);
        Assert.Contains("explicit serverUrl argument", result);
    }

    [Fact]
    public async Task ActivateAgent_EmptyProjectPath_ReturnsError()
    {
        var result = await AgentTools.ActivateAgent(projectPath: "");
        Assert.Contains("Error", result);
        Assert.Contains("projectPath", result);
    }
}
