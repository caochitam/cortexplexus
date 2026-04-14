using System.ComponentModel;
using Microsoft.AspNetCore.Http;
using ModelContextProtocol.Server;

namespace CortexPlexus.App.Mcp.Tools;

[McpServerToolType]
public sealed class AgentTools
{
    [McpServerTool, Description(
        "Activate the CortexPlexus Local Indexing Agent for a project. " +
        "Returns a structured, step-by-step recipe for the AI assistant to execute LOCALLY via Bash. " +
        "The agent parses source on the user's machine (Roslyn for C#, Tree-sitter for other languages) " +
        "and uploads only metadata — the source never leaves the dev machine. " +
        "Server URL auto-detected from the MCP connection; the AI should not guess or assume localhost.")]
    public static string ActivateAgent(
        [Description("Absolute path to the project directory on the AI client's local machine.")] string projectPath,
        [Description("Project name (used as identifier in CortexPlexus). Default: basename of projectPath.")] string? projectName = null,
        [Description("Override server URL. Leave unset to auto-detect from this MCP connection — recommended.")] string? serverUrl = null,
        IHttpContextAccessor? httpContextAccessor = null)
    {
        if (string.IsNullOrWhiteSpace(projectPath))
            return "Error: projectPath is required (absolute path to the project on the user's machine).";

        var name = projectName ?? Path.GetFileName(projectPath.TrimEnd('/', '\\'));

        // Resolution order:
        //   1. explicit serverUrl argument (highest precedence — caller knows what they want)
        //   2. auto-detect from the incoming MCP HTTP request (this is almost always right —
        //      it's the URL the AI client already successfully connected to)
        //   3. CORTEXPLEXUS_SERVER_URL env var on the server (rare; mostly for dev)
        //   4. last-resort localhost:8080 (only useful if server and AI client are on the same host)
        var req = httpContextAccessor?.HttpContext?.Request;
        var detectedUrl = req is null ? null : $"{req.Scheme}://{req.Host}";

        string server;
        string urlSource;
        if (!string.IsNullOrWhiteSpace(serverUrl))
        {
            server = serverUrl.TrimEnd('/');
            urlSource = "explicit serverUrl argument";
        }
        else if (!string.IsNullOrWhiteSpace(detectedUrl))
        {
            server = detectedUrl.TrimEnd('/');
            urlSource = "auto-detected from your MCP connection Host header";
        }
        else if (Environment.GetEnvironmentVariable("CORTEXPLEXUS_SERVER_URL") is { Length: > 0 } envUrl)
        {
            server = envUrl.TrimEnd('/');
            urlSource = "CORTEXPLEXUS_SERVER_URL environment variable on the server";
        }
        else
        {
            server = "http://localhost:8080";
            urlSource = "fallback default (server and client likely on the same host)";
        }

        // Platform detection (Windows path markers vs POSIX)
        var isWindows = projectPath.Contains('\\') || (projectPath.Length >= 2 && projectPath[1] == ':');
        var platformTag = isWindows ? "win-x64" : "linux-x64";
        var installDir = isWindows
            ? "$USERPROFILE/.cortexplexus/agent"
            : "$HOME/.cortexplexus/agent";
        var agentDll = $"{installDir}/cortexplexus-agent.dll";
        var runningCheck = isWindows
            ? "tasklist /FI \"IMAGENAME eq dotnet.exe\" /FO CSV 2>/dev/null | grep -i cortexplexus-agent || echo NOT_RUNNING"
            : "pgrep -f cortexplexus-agent || echo NOT_RUNNING";

        // Output is structured as a decision tree so an AI agent can parse it
        // linearly and each step has a clear PASS/FAIL signal.
        return $$"""
            # Activate CortexPlexus Local Agent — AI Execution Recipe

            > Execute these Bash commands on the USER'S machine, in order.
            > Each step has a check (PASS/FAIL marker) — branch accordingly.
            > Do NOT skip the connectivity verification — it catches the most common cause of silent failures.

            ## Server URL: `{{server}}`

            Determined by: **{{urlSource}}**.

            If this is wrong (e.g. you know the server is actually somewhere else, or the user said so),
            stop and re-invoke `ActivateAgent` with `serverUrl: "<correct URL>"`.

            ## Project: `{{name}}` at `{{projectPath}}`

            Platform inferred: **{{platformTag}}** (from the path style).

            ---

            ## Step 1 — Verify prerequisites

            ```bash
            dotnet --version
            ```
            **PASS**: prints a version (e.g. `9.0.1`) → continue.
            **FAIL**: command not found → tell the user to install .NET SDK from https://dotnet.microsoft.com/download, then retry.

            ## Step 2 — Confirm the server is reachable from this machine

            ```bash
            curl -sS -o /dev/null -w "%{http_code}\n" {{server}}/api/agent/version
            ```
            **PASS**: prints `200` → continue.
            **FAIL** (`000` / `connection refused` / `404`):
            - Run `curl -v {{server}}/api/agent/version` once to get a real error line.
            - If hostname/port is actually different, re-invoke `ActivateAgent` with `serverUrl: "http://<right-host>:<port>"`.
            - If the server is down, tell the user — DO NOT fabricate a workaround.

            ## Step 3 — Version check (skip if no agent installed yet)

            ```bash
            if [ -f "{{agentDll}}" ]; then
              INSTALLED=$(dotnet "{{agentDll}}" version 2>/dev/null | grep -oE 'v[0-9.]+' | tr -d 'v' || echo "unknown")
              LATEST=$(curl -sS {{server}}/api/agent/version | python3 -c 'import sys,json; print(json.load(sys.stdin)["version"])' 2>/dev/null || echo "unknown")
              if [ "$INSTALLED" = "$LATEST" ]; then echo "UP_TO_DATE:$INSTALLED"
              elif [ "$INSTALLED" = "unknown" ] || [ "$LATEST" = "unknown" ]; then echo "CHECK_FAILED"
              else echo "UPDATE_AVAILABLE:$INSTALLED -> $LATEST"; fi
            else
              echo "NOT_INSTALLED"
            fi
            ```
            - `UP_TO_DATE:X` → skip to Step 5 (no need to re-install).
            - `UPDATE_AVAILABLE:X -> Y` → proceed to Step 4 to re-install. The user's AI agent should notify the user: "CortexPlexus agent update available: X → Y".
            - `NOT_INSTALLED` → proceed to Step 4 (first install).
            - `CHECK_FAILED` → treat as "proceed with caution"; log it but don't block the user. Likely a version-format mismatch; re-install does no harm.

            ## Step 4 — Is an agent already running for any project?

            ```bash
            {{runningCheck}}
            ```
            **If a process is listed**: an agent is already running. If you know it's for `{{name}}`, jump to Step 6 (Verify). If it's for a different project, leave it alone — CortexPlexus supports multiple concurrent agents per host.
            **If `NOT_RUNNING`**: continue.

            ## Step 5 — Install or upgrade the agent

            Run this if Step 3 said `NOT_INSTALLED` or `UPDATE_AVAILABLE:*`.
            Skip if Step 3 said `UP_TO_DATE:*`.

            ```bash
            mkdir -p "{{installDir}}"
            curl -fsSL "{{server}}/api/agent/download?platform={{platformTag}}" -o /tmp/cortexplexus-agent.tar.gz \
              && tar -xzf /tmp/cortexplexus-agent.tar.gz -C "{{installDir}}" \
              && rm /tmp/cortexplexus-agent.tar.gz \
              && test -f "{{agentDll}}" && echo "INSTALL_OK" || echo "INSTALL_FAILED"
            ```
            **PASS**: prints `INSTALL_OK` → continue.
            **FAIL** (`INSTALL_FAILED`, non-zero curl, missing dll):
            - `curl -fsSI {{server}}/api/agent/download?platform={{platformTag}}` to check the download endpoint.
            - If 404, tell the user the server build is missing the {{platformTag}} binary and suggest `docker compose pull` on the server.
            - Do NOT proceed.

            ## Step 6 — One-time index, then watch mode

            First, do a foreground index so the user (and you) can SEE any errors:

            ```bash
            dotnet "{{agentDll}}" index "{{projectPath}}" --server {{server}} --name "{{name}}"
            ```
            **PASS**: the agent prints a final summary with non-zero `Symbols` + `EmbeddingsPersisted == Symbols` (or close to it) and exits 0.
            **FAIL**:
            - `EmbeddingsPersisted < Symbols` → server reported partial persist; stop and call `ListRepositories()` to inspect health. Re-run only after the underlying issue is fixed.
            - Agent hangs for > 5 min on a small project → server embedding endpoint may be unreachable from the Docker container. Check server logs.
            - Exit code non-zero → read the last 20 lines of stdout; do NOT silently retry.

            If the initial index succeeded, start watch mode in the background:

            ```bash
            nohup dotnet "{{agentDll}}" watch "{{projectPath}}" --server {{server}} --name "{{name}}" > /tmp/cortexplexus-agent-{{name}}.log 2>&1 &
            echo "Watch started (PID $!). Logs: /tmp/cortexplexus-agent-{{name}}.log"
            ```

            ## Step 7 — Verify end-to-end

            ```bash
            sleep 2
            curl -sS {{server}}/api/repositories | grep -i '"name":"{{name}}"' && echo "REGISTERED" || echo "NOT_REGISTERED"
            ```
            **If REGISTERED**: call `ListRepositories()` from your MCP client — look for `Health: OK` on the `{{name}}` entry. If it says `PARTIAL` / `DEGRADED` / `EMPTY`, indexing is incomplete and queries may miss hits.
            **If NOT_REGISTERED**: the server never received anything; re-check `curl {{server}}/api/agent/version` and `tail /tmp/cortexplexus-agent-{{name}}.log`.

            ---

            ## Reference: other agent commands

            ```bash
            dotnet "{{agentDll}}" status                # list running agents (all projects)
            dotnet "{{agentDll}}" stop --all            # stop every running agent on this host
            dotnet "{{agentDll}}" index "<path>" --server {{server}}   # one-shot re-index (no watch)
            dotnet "{{agentDll}}" watch "<path>" --server {{server}}   # incremental watch mode
            ```

            ## Troubleshooting pointers (use only if a step above fails)

            - `{{server}}` unreachable from the user's machine but fine from the server host → NAT / firewall / Docker bridge problem, not a CortexPlexus issue.
            - Agent runs, uploads symbols, but `ListRepositories()` shows `Health: DEGRADED` → the server's embedding provider (Ollama / Gemini) is failing. Check server logs for `Failed to embed batch`.
            - Re-indexing the same project twice shows no new symbols → incremental indexing is working correctly; forcing a full re-index requires deleting the repository row server-side.
            """;
    }
}
