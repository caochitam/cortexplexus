using System.ComponentModel;
using ModelContextProtocol.Server;

namespace CortexPlexus.App.Mcp.Tools;

[McpServerToolType]
public sealed class HelpTools
{
    private static readonly string[] ValidTopics =
    {
        "quick-start", "tools", "indexing", "strategies", "when-to-use", "all"
    };

    [McpServerTool, Description(
        "Get usage guide for CortexPlexus MCP tools. " +
        "Call this FIRST when connecting to CortexPlexus to learn how to use all available tools effectively.")]
    public static string GetHelp(
        [Description("Topic: 'quick-start', 'tools', 'indexing', 'strategies', 'when-to-use', or 'all' (default)")] string topic = "all")
    {
        var normalized = topic.ToLowerInvariant();
        var content = normalized switch
        {
            "quick-start" => QuickStart,
            "tools" => ToolReference,
            "indexing" => IndexingGuide,
            "strategies" => Strategies,
            "when-to-use" => WhenToUse,
            "all" => $"{QuickStart}\n\n{WhenToUse}\n\n{ToolReference}\n\n{IndexingGuide}\n\n{Strategies}",
            _ => null
        };

        // R22 Fix #12: warn loudly when an unknown topic is passed instead of
        // silently returning the default. Avoids the user thinking their topic
        // filter was applied when it was ignored.
        if (content is null)
        {
            var validList = string.Join(", ", ValidTopics.Select(t => $"'{t}'"));
            return $"Unknown topic '{topic}'. Valid topics: {validList}.\n\n" +
                   $"Showing 'all' as default fallback:\n\n" +
                   $"{QuickStart}\n\n{WhenToUse}\n\n{ToolReference}\n\n{IndexingGuide}\n\n{Strategies}";
        }

        return content;
    }

    private const string QuickStart = """
        # CortexPlexus — Quick Start

        CortexPlexus is a Code Intelligence MCP Server. It builds a Knowledge Graph
        from source code and provides structured context — call graphs, DI wiring,
        API routes, class hierarchy, semantic search — in 1 tool call instead of
        10+ grep/read operations.

        ## Step 1: Activate Local Agent (source code stays on your machine)
        → ActivateAgent(projectPath: "<your workspace path>")
        Follow the returned instructions to install + start the agent.
        The agent parses code locally and sends only metadata to server.

        ## Step 2: Verify indexing
        → ListRepositories()

        ## Step 3: Explore
        → OnboardProject("repo-name")               # full project overview
        → ExploreTopic("ServiceName", depth:"deep")  # deep dive into a symbol

        ## Alternative indexing
        → IndexFromLocal("/path/to/project")        # code already on server
        → IndexFromGit("https://github.com/...")     # clone from Git

        Supported: C# (Roslyn), TypeScript, JavaScript, Python, Java, Go, Rust, PHP
        """;

    private const string WhenToUse = """
        # When to Use MCP Instead of Grep/Read

        ALWAYS prefer MCP tools over grep/read for these tasks:

        | You want to...                  | Use MCP tool                              | NOT this                    |
        |---------------------------------|-------------------------------------------|-----------------------------|
        | Find a class/method by name     | SearchCode("name")                        | Grep + Read files           |
        | Find code by concept            | SemanticSearch("payment logic")           | Grep guessing keywords      |
        | Understand a service deeply     | ExploreTopic("name", depth:"deep")        | Read 10+ files manually     |
        | See who calls a method          | GetCallers("FQN")                         | Grep method name            |
        | Trace what a method calls       | GetCallees("FQN")                         | Read method + follow calls  |
        | Check change impact             | GetImpactAnalysis("FQN", depth:3)         | Grep + manual trace         |
        | Find interface implementations  | GetImplementations("IService")            | Grep class name             |
        | See inheritance tree            | GetClassHierarchy("ClassName")            | Read multiple files         |
        | View DI registrations           | GetDiRegistrations("service")             | Read Startup.cs/Program.cs  |
        | List API endpoints              | GetApiEndpoints()                         | Grep "MapGet" / "[HttpGet]" |
        | Trace request flow              | GetDataFlow("/api/route")                 | Read endpoint → handler     |
        | Check test coverage             | GetTestCoverage("Method.FQN")             | Grep test files             |
        | Find config usage               | GetConfigUsage("KEY")                     | Grep "KEY" in all files     |
        | Find unused code                | GetDeadCode("repo")                       | Manual analysis             |
        | Check middleware order           | GetMiddlewarePipeline()                   | Read Program.cs             |
        | Detect circular dependencies    | GetCircularDependencies("repo")           | Manual DI analysis          |
        | Onboard new project             | OnboardProject("repo")                    | Read 20+ files              |

        Still use grep/read when:
        - Reading a specific file you know the path to
        - Making code edits (MCP is read-only)
        - Checking git history (git log, git blame)

        Rule of thumb: If you're about to grep for a class/method name, use SearchCode
        or ExploreTopic instead. If you're about to read multiple files to trace a flow,
        use a graph traversal tool.
        """;

    private const string ToolReference = """
        # Tool Reference (30 tools)

        ## INDEXING (do this first)
        - ActivateAgent(projectPath, projectName?)
          Install + start local indexing agent. Source never leaves your machine.
          Example: ActivateAgent(projectPath: "/home/dev/myproject")

        - IndexFromLocal(path)
          Index a directory already on server.
          Example: IndexFromLocal("/workspace/myproject")

        - IndexFromGit(url, branch?, name?)
          Clone from Git URL and index.
          Example: IndexFromGit("https://github.com/org/repo.git", branch: "develop")

        ## COMPOSITE (prefer these — 1 call replaces 4-5 calls)
        - ExploreTopic(query, repository?, depth?)
          Deep-explore a symbol: search → callers → deps → callees → impls.
          depth: "shallow" (search only) | "normal" (+callers/deps) | "deep" (everything)
          Example: ExploreTopic("OrderService", repository: "backend", depth: "deep")

        - OnboardProject(repository)
          Full project overview: DI registrations + API endpoints + EF entities.
          Example: OnboardProject("OpsFlow.Api")

        ## SEARCH
        - SearchCode(query, repository?, limit?, expand?)
          BM25 full-text search. Use when you know the exact name.
          Example: SearchCode("IPaymentService", repository: "backend", limit: 10)

        - SemanticSearch(query, repository?, limit?, expand?)
          Vector + BM25 hybrid. Use for natural language queries.
          Example: SemanticSearch("authentication and authorization logic")
          Tip: expand=true uses Ollama LLM for better recall (+2s latency)

        ## GRAPH TRAVERSAL
        - GetCallers(methodFqn, depth?)         — Who calls this? (depth 1-5)
        - GetCallees(methodFqn, depth?)         — What does this call? (depth 1-5)
        - GetDependencies(fqn, depth?)          — Type deps: DI injection, field types (depth 1-3)
        - GetImplementations(interfaceFqn)      — Classes implementing interface
        - GetClassHierarchy(classFqn)           — Inheritance tree (ancestors + descendants)
        - GetImpactAnalysis(fqn, depth?)        — Blast radius: callers + refs + impls + hierarchy

        Tip: Use SearchCode("partial-name") first to find the correct FQN,
        then pass FQN to graph tools.

        ## .NET DEEP ANALYSIS
        - GetDiRegistrations(serviceType?, repository?)  — DI container (AddScoped/Transient/Singleton)
        - GetEntityMapping(entityName?, repository?)     — EF Core entities (DbContext → DbSet → Entity)
        - GetApiEndpoints(moduleName?, repository?)      — API routes (Minimal API + MVC controllers)
        - GetDataFlow(endpointRoute)                     — Trace: endpoint → handler → services
        - GetNuGetAudit(path?)                           — NuGet package versions
        - GetArchitecture(repository?)                   — Architecture overview (modules + DI + endpoints)

        ## CODE QUALITY & ARCHITECTURE
        - GetTestCoverage(methodFqn)                     — Which tests cover this production method?
        - GetConfigUsage(configKey?, repository?)         — Where is this config key used? (8 languages)
          Supports: appsettings.json, .env, IConfiguration, IOptions<T>, process.env,
          os.environ, System.getenv, os.Getenv, env::var, $_ENV
        - GetDeadCode(repository)                        — Public/internal methods with 0 callers
        - GetMiddlewarePipeline(repository?)              — ASP.NET middleware execution order
        - GetCircularDependencies(repository)            — Detect cyclic class dependencies (A→B→C→A)

        ## OVERVIEW
        - ListRepositories()    — All indexed repos with last index time
        - GetHelp(topic?)       — This guide (topics: quick-start, tools, indexing, strategies, when-to-use)

        ## AGENT MEMORY (opt-in — requires Memory__Enabled=true)
        Semantic, scoped, auto-decaying memory store. See docs/MEMORY-SYSTEM.md.
        - SaveMemory(content, scope, scopeId?, topic?, importance?, relatedFqns?)
          Store a memory. Scope: session|project|global. Topic shapes decay.
          Example: SaveMemory("Team prefers async/await", scope: "project", scopeId: "<repoId>", topic: "preference")
        - RecallMemory(query, scope?, scopeId?, topic?, relatedFqn?, limit?)
          Semantic recall with decay-weighted ranking.
          Example: RecallMemory("auth patterns", scope: "project", scopeId: "<repoId>")
        - ListMemories(scope?, scopeId?, topic?, limit?)
          Audit all saved memories (no semantic search; includes near-forgotten).
        - ForgetMemory(id)  — Delete a specific memory by UUID.
        """;

    private const string IndexingGuide = """
        # Indexing Guide

        ## When to index
        - First time connecting to a new project
        - After major code changes (new files, refactoring)
        - Not needed if local agent is already running (it auto-reindexes)

        ## Methods (in order of preference)
        1. LOCAL AGENT (best — source stays on your machine):
           → ActivateAgent("/path/to/project")
           Agent downloads automatically, watches files, indexes on save.

        2. Code on server:
           → IndexFromLocal("/path/to/project")

        3. Code on GitHub/Azure DevOps:
           → IndexFromGit("https://github.com/org/repo.git")

        4. Code on another machine (REST API):
           git archive -o project.zip HEAD
           curl -X POST http://server:8080/api/index/push -F "archive=@project.zip"

        ## Rules
        - 1 path = 1 repository (point to root directory)
        - Incremental: only changed files are re-parsed (SHA256 hash comparison)
        - Use 'repository' parameter in search/analysis tools to scope to specific project

        ## After indexing
        → ListRepositories()     — verify project appears
        → OnboardProject("repo") — get full overview
        """;

    private const string Strategies = """
        # Usage Strategies

        ## Session start (every session)
        1. ActivateAgent(projectPath: "<workspace>") — if not already running
        2. ListRepositories() — verify indexed

        ## Multi-repo workflow
        When working with multiple projects, ALWAYS pass repository parameter:
          SearchCode("UserService", repository: "backend")
          GetApiEndpoints(repository: "OpsFlow.Api")
        Tools using FQN (GetCallers, etc.) don't need repository — FQN is already unique.

        ## Understand a class/service (1 call)
        → ExploreTopic("ClassName", depth: "deep")
        Replaces: SearchCode + GetCallers + GetDependencies + GetCallees + GetImplementations

        ## Onboard new project (1 call)
        → OnboardProject("repo")
        Replaces: GetArchitecture + GetApiEndpoints + GetDiRegistrations + GetEntityMapping

        ## Evaluate change impact
        → GetImpactAnalysis("Method.FQN", depth: 3)
        → GetTestCoverage("Method.FQN")  — check if tests exist

        ## Debug request flow
        → GetDataFlow("/api/endpoint")
        → GetMiddlewarePipeline()  — check middleware order

        ## Code quality review
        → GetDeadCode("repo")                — unused methods
        → GetCircularDependencies("repo")    — tight coupling
        → GetConfigUsage("KEY")              — config impact analysis

        ## Priority: composite > individual tools
        Always try ExploreTopic or OnboardProject first.
        Use individual tools only for very specific queries.
        """;
}
