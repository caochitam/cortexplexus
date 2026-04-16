# CortexPlexus MCP — Guide for AI Agents

> **You are reading this because CortexPlexus MCP Server is available.**
> It gives you structured code intelligence — call graphs, DI wiring, API routes, class hierarchy, semantic search — in **1 tool call** instead of 10+ grep/read operations.

---

## 1. Why Use MCP Instead of Grep/Read

| Task | Without MCP | With MCP | Savings |
|------|-------------|----------|---------|
| Find callers of a method | Grep name → read 5-10 files → trace manually | `GetCallers("FQN")` | 1 call vs 10+ |
| Understand a service | Read 5+ files (class + deps + tests) | `ExploreTopic("ServiceName", depth:"deep")` | 1 call vs 15+ |
| Trace API request flow | Read Program.cs → find handler → read handler → trace calls | `GetDataFlow("/api/endpoint")` | 1 call vs 8+ |
| Find DI wiring | Grep "AddScoped" → read Startup.cs → match interfaces | `GetDiRegistrations("IService")` | 1 call vs 5+ |
| Check impact of change | Grep → read callers → read their callers → manual analysis | `GetImpactAnalysis("FQN", depth:3)` | 1 call vs 10+ |
| Find which tests cover code | Grep test files → read each → check calls | `GetTestCoverage("Method.FQN")` | 1 call vs 5+ |
| Understand project structure | Read 20+ files → mentally assemble | `OnboardProject("repo")` | 1 call vs 20+ |
| Find config usage | Grep "ConnectionString" across all files | `GetConfigUsage("ConnectionStrings")` | 1 call vs 5+ |

**Rule of thumb:** If you're about to grep for a class/method name, use `SearchCode` or `ExploreTopic` instead. If you're about to read multiple files to trace a flow, use a graph traversal tool.

---

## 2. Connect (One-Time Setup)

CortexPlexus MCP is **not** pre-installed. Create the config file for your client:

| Client | File | Location |
|--------|------|----------|
| **Claude Code** | `.mcp.json` | Project root (copy from `.mcp.json.example`) |
| **Cursor** | `.cursor/mcp.json` | Project root |
| **VS Code** | `.vscode/mcp.json` | Project root |
| **Google Antigravity** | `mcp_config.json` | `~/.gemini/antigravity/` (Windows: `C:\Users\<USERNAME>\.gemini\antigravity\`) |
| **Windsurf** | `~/.codeium/windsurf/mcp_config.json` | Home dir |

All four project-scoped config files above are **git-ignored** — your personal URL/auth never hits the repo.

**Claude Code** — copy the template and edit the URL if needed:
```bash
cp .mcp.json.example .mcp.json    # then open it and change host if not localhost
```
Template contents:
```json
{
  "mcpServers": {
    "cortexplexus": {
      "type": "http",
      "url": "http://localhost:8080/mcp"
    }
  }
}
```

**Cursor** — `.cursor/mcp.json`:
```json
{
  "mcpServers": {
    "cortexplexus": {
      "url": "http://localhost:8080/mcp"
    }
  }
}
```

**VS Code** — `.vscode/mcp.json` (note: key is `"servers"`, not `"mcpServers"`):
```json
{
  "servers": {
    "cortexplexus": {
      "type": "http",
      "url": "http://localhost:8080/mcp"
    }
  }
}
```

**Google Antigravity** — `mcp_config.json` in `~/.gemini/antigravity/` (Windows: `C:\Users\<USERNAME>\.gemini\antigravity\mcp_config.json`). **Different schema from every other client above** — Antigravity uses `serverUrl`, **not** `url`, and does not need a `type` field:
```json
{
  "mcpServers": {
    "cortexplexus": {
      "serverUrl": "http://localhost:8080/mcp"
    }
  }
}
```
Two equivalent ways to open the file:
1. Edit the path above directly in any editor.
2. In Antigravity: Agent panel `...` menu → **MCP Servers** → **Manage MCP Servers** → **View raw config**.

If the server is on a remote host (e.g. LXC / VM / another dev machine), replace `localhost` with the IP or hostname. For authenticated remote endpoints, add headers:
```json
{
  "mcpServers": {
    "cortexplexus": {
      "serverUrl": "http://<host>:8080/mcp",
      "headers": { "Authorization": "Bearer <token>" }
    }
  }
}
```

**After creating the file: close and reopen your IDE.** No client hot-reloads MCP config.

> **Note for AI agents assisting users:** If a user asks how to connect CortexPlexus from Antigravity, walk them through the schema above step by step — do NOT copy the Claude Code / Cursor JSON (which uses `url`). The `serverUrl` key is the single most common mistake when setting up Antigravity MCP.

### Developing on CortexPlexus itself

If you're hacking on CortexPlexus, you're the rare case where the IDE *and* the MCP server point at the same repo. A few rules to keep personal config out of the public repo:

- **Never commit `.mcp.json`** — it's git-ignored. Edit freely; your URL/headers stay local.
- **Never commit `.claude/`, `.vscode/mcp.json`, `.cursor/`** — also git-ignored.
- **Config precedence (important):** if a project-level `.mcp.json` exists in the repo, it **wins over** user scope. So if you already ran `cp .mcp.json.example .mcp.json` during Quick Start, a subsequent `claude mcp add --scope user` will be silently shadowed. Two clean options:
  - **Simplest:** just edit the local `.mcp.json` (it's gitignored, won't leak).
  - **Or:** delete `.mcp.json` and use `claude mcp add --scope user --transport http cortexplexus http://<server>:8080/mcp` — writes to `~/.claude.json`, never touches the repo.
- **Avoid `--scope project` CLI.** It writes to the tracked `.mcp.json.example` (or re-creates `.mcp.json`) in ways that are easy to commit by accident. Stick to editing the gitignored `.mcp.json` by hand, or `--scope user`.
- **Running IDE on a different host than the server?** Change `localhost` in your `.mcp.json` to the server IP or hostname. The `.mcp.json.example` stays `localhost` so fresh clones work out-of-the-box.

---

## 3. First 3 Commands (Memorize This)

```
1. ActivateAgent(projectPath: "<workspace>")   → Install + start local indexing agent
2. ListRepositories()                           → Verify project is indexed (check the Health: line)
3. GetHelp("tools")                             → Learn all 26 available tools
```

That's it. The agent indexes your code locally (source never leaves your machine), and `GetHelp()` teaches you everything else.

The Health line on every entry is your early-warning signal: `OK` means the repo is fully queryable, `PARTIAL` / `DEGRADED` / `EMPTY` mean some semantic hits will be missing — see [`HEALTH-METRICS.md`](HEALTH-METRICS.md) for what each label means and how to recover. For best practices on the agent itself (single-`.sln` indexing, watch mode, throughput tuning), see [`runbooks/agent-best-practices.md`](runbooks/agent-best-practices.md).

> **Add to your system prompt:**
> *"At session start, call ActivateAgent() then GetHelp() from CortexPlexus MCP."*

---

## 4. When to Use MCP (Decision Guide)

**ALWAYS prefer MCP tools over grep/read for these tasks:**

| You want to... | Use this MCP tool | NOT this |
|----------------|-------------------|----------|
| Find a class/method by name | `SearchCode("name")` | `Grep("name")` + Read files |
| Find code by concept | `SemanticSearch("payment logic")` | Grep guessing keywords |
| Understand a service deeply | `ExploreTopic("name", depth:"deep")` | Read 10+ files |
| See who calls a method | `GetCallers("FQN")` | Grep method name |
| Trace what a method calls | `GetCallees("FQN")` | Read the method + follow calls |
| Check change impact | `GetImpactAnalysis("FQN")` | Manual grep + trace |
| Find interface implementations | `GetImplementations("IService")` | Grep class name |
| See inheritance tree | `GetClassHierarchy("ClassName")` | Read multiple files |
| View DI registrations | `GetDiRegistrations()` | Read Program.cs/Startup.cs |
| List API endpoints | `GetApiEndpoints()` | Grep "MapGet" or "[HttpGet]" |
| Trace request flow | `GetDataFlow("/api/route")` | Read endpoint → handler → service chain |
| Check test coverage | `GetTestCoverage("Method.FQN")` | Grep test files |
| Find config dependencies | `GetConfigUsage("KEY")` | Grep "KEY" across all files |
| Find dead code | `GetDeadCode("repo")` | Manual analysis |
| Check middleware order | `GetMiddlewarePipeline()` | Read Program.cs |
| Detect circular deps | `GetCircularDependencies("repo")` | Manual DI analysis |
| Onboard new project | `OnboardProject("repo")` | Read 20 files |

**Still use grep/read when:**
- Reading a specific file you already know the path to
- Making code edits (MCP is read-only)
- Checking git history (`git log`, `git blame`)

---

## 5. Tool Quick Reference (26 tools)

For detailed parameters and examples, call `GetHelp("tools")`.

```
INDEXING          ActivateAgent · IndexFromLocal · IndexFromGit
COMPOSITE         ExploreTopic · OnboardProject
SEARCH            SearchCode · SemanticSearch
GRAPH             GetCallers · GetCallees · GetDependencies
                  GetImplementations · GetClassHierarchy · GetImpactAnalysis
.NET ANALYSIS     GetDiRegistrations · GetEntityMapping · GetApiEndpoints
                  GetDataFlow · GetNuGetAudit · GetArchitecture
CODE QUALITY      GetTestCoverage · GetConfigUsage · GetDeadCode
                  GetMiddlewarePipeline · GetCircularDependencies
OVERVIEW          ListRepositories · GetHelp
```

**Languages:** C# (Roslyn deep semantic), TypeScript, JavaScript, Python, Java, Go, Rust, PHP (Tree-sitter)

---

## 6. Multi-Repo Workflow

When working with multiple projects, **always pass `repository` parameter** to scope results:

```
ListRepositories()                                    → See available repos
SearchCode("UserService", repository: "backend")      → Only backend results
GetApiEndpoints(repository: "OpsFlow.Api")             → Only this project's endpoints
```

Tools using FQN (GetCallers, GetCallees, etc.) don't need `repository` — FQN is already unique.

---

## 7. Troubleshooting

| Problem | Fix |
|---------|-----|
| "No results" | Project not indexed → call `ActivateAgent()` first |
| FQN not found | Use `SearchCode("partial-name")` to find correct FQN |
| Semantic search fails | Ollama not running → `curl http://localhost:11434/api/tags` |
| Connection refused | Server not running → `docker compose up -d` on server |
| Tools not visible | Wrong config file or missing session restart |
