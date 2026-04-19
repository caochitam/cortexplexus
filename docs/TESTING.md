# Testing Strategy

CortexPlexus has **693 automated tests** across 10 test projects, covering unit, integration, and end-to-end validation with real-world codebases.

## Test architecture

```
                          ┌─────────────────────────────┐
                          │   CI Pipeline (GitHub Actions) │
                          │   Runs on every push & PR      │
                          └─────────────┬─────────────────┘
                                        │
              ┌─────────────────────────┼─────────────────────────┐
              │                         │                         │
     ┌────────┴────────┐     ┌──────────┴──────────┐    ┌────────┴────────┐
     │   Unit Tests    │     │ Integration Tests    │    │ Smoke Tests     │
     │   (no infra)    │     │ (real PostgreSQL +   │    │ (real MCP calls │
     │                 │     │  Apache AGE via      │    │  against live   │
     │   576 tests     │     │  Testcontainers)     │    │  deployment)    │
     │   < 5 seconds   │     │                      │    │                 │
     └────────┬────────┘     │  117 tests            │    │  Manual, R17-R25│
              │              │  ~2 minutes           │    │  30 tools tested│
              │              └──────────┬──────────┘    └─────────────────┘
              │                         │
              └─────────────────────────┘
```

## Test suites (10 projects)

| Suite | Tests | Type | What it covers |
|-------|------:|------|----------------|
| **CortexPlexus.Core.Tests** | 33 | Unit | Domain models, secrets scanner, validation |
| **CortexPlexus.Parsing.Tests** | 184 | Unit | Roslyn extractors (call graph, symbols, types, DI, EF Core, middleware, config, metrics, exceptions, HTTP calls), Tree-sitter parsers (TS/JS/Python/Java/Go/Rust/PHP), Markdown parser, ignore patterns |
| **CortexPlexus.Search.Tests** | 80 | Unit | Hybrid query router, BM25/vector fusion, RRF ranking, context compressor, query preprocessor, query expansion |
| **CortexPlexus.Mcp.Tests** | 123 | Unit | All 26 MCP tool handlers, parameter validation, friendly error messages, framework noise filter, repo resolver de-dup, "did you mean" hint logic |
| **CortexPlexus.App.Tests** | 32 | Unit | Indexing pipeline, embedding batch helper (parallel execution, resilience, sanitization) |
| **CortexPlexus.Embedding.Tests** | 26 | Unit | Gemini + Ollama embedding services (HTTP mocking), provider-aware parallelism defaults, retry/timeout behavior |
| **CortexPlexus.Agent.Tests** | 72 | Unit | Local indexer (chunking, file hash, solution/csproj discovery, orphan project detection), agent updater, file watcher, PID management |
| **CortexPlexus.Api.Tests** | 27 | Unit | REST API endpoints via in-memory test server (TestApiFactory) |
| **CortexPlexus.Graph.Tests** | 80 | Integration | AgeGraphStore against real Apache AGE container: node/edge upsert, callers/callees traversal, class hierarchy, dead code detection, HNSW bulk-load, FQN dedup, kind resolution, API endpoint filtering |
| **CortexPlexus.Integration.Tests** | 37 | Integration | VectorStore + FullTextStore against real pgvector/tsvector: search, upsert, delete, HNSW bulk-load, BM25 ranking |

**Total: 693 tests** | Unit: 576 (~1s each) | Integration: 117 (~2min, requires Docker)

## Integration test infrastructure

Integration tests use [Testcontainers](https://testcontainers.com/) to spin up real database containers:

- **`AgeFixture`** — starts `apache/age:latest` container with full schema (graph + relational tables). Each test gets a clean state via `CleanAsync()`.
- **`PostgresFixture`** — starts `pgvector/pgvector:pg17` container for vector + full-text search tests. Includes HNSW index, tsvector columns, all migration-applied columns.

No mocking of database behavior — tests run real SQL + real Cypher queries against real PostgreSQL.

## Test patterns used

### MCP tool testing (NSubstitute mocks)
```csharp
var graphStore = Substitute.For<IGraphStore>();
graphStore.QueryCallersAsync(Arg.Any<string>(), ...)
    .Returns(Task.FromResult<IReadOnlyList<SearchResult>>([
        TestHelpers.MakeResult("App.Caller.Method()", "Method", "method"),
    ]));

var result = await GraphTraversalTools.GetCallers(
    methodFqn: "App.Target()", graphStore: graphStore, compressor: compressor);

Assert.Contains("App.Caller.Method()", result);
```

### HTTP service testing (FakeHttpMessageHandler)
```csharp
var handler = FakeHttpMessageHandler.Ok("""{"embeddings":[[0.5,0.6,0.7]]}""");
var service = BuildService(handler);

var result = await service.EmbedAsync("test");

Assert.Equal(3, result.Length);
Assert.Equal(1, handler.CallCount);
```

### Graph integration testing (real Apache AGE)
```csharp
[Collection("Age")]
public class AgeGraphStoreIntegrationTests(AgeFixture fixture)
{
    [Fact]
    public async Task QueryCallees_DiamondCallGraph_NoDuplicates()
    {
        // Real graph: A → B → D and A → C → D
        await _store.UpsertNodesAsync(new[] { a, b, c, d });
        await _store.UpsertEdgesAsync(new[] { aToB, aToC, bToD, cToD });

        var callees = await _store.QueryCalleesAsync("App.A()", depth: 2);

        // D reachable via 2 paths but must appear only once (R25 dedup fix)
        Assert.Equal(1, callees.Count(c => c.Fqn == "App.D()"));
    }
}
```

## Real-world validation

Beyond automated tests, CortexPlexus has been validated against real production codebases through 9 rounds of manual smoke testing (R17–R25):

### Test project: CortexFlow

A production .NET 10 application with:
- **6,912 symbols** (classes, methods, interfaces, properties, fields, events, constructors, enums, records)
- **15,023 relationships** (Calls, Implements, Inherits, DependsOn, UsesType, HandledBy, MapsTo, ReadsConfig, TestCovers, Subscribes, HttpCalls, PipelineOrder)
- **85 API endpoints** across 12 controllers
- **96 DI registrations**
- **36 EF Core entity mappings**
- **41 test methods** across 1 test project
- **7 middleware pipeline stages**

### Validation results

| Category | Queries tested | Accuracy |
|----------|---------------|----------|
| Code search (BM25 + vector) | 50+ queries | Results match manual grep |
| Call graph (callers + callees) | 20+ methods | Correct, including interface resolution |
| Class hierarchy (Inherits + Implements) | 10+ classes | No sibling bleeding, directional traversal |
| DI registrations | Full scan | 96/96 match Program.cs |
| API endpoints | Full scan + module filter | 85/85 match controller routes |
| EF Core entity mappings | Full scan | 36/36 match DbContext |
| Dead code detection | Full scan | 269 candidates after filtering HTTP endpoints + test methods |
| Middleware pipeline | Full scan | 7 stages, exact match with Program.cs (no false positives) |
| Test coverage | Per-method queries | TestCovers edges link correctly |
| Config usage | Key-based queries | Both config file nodes + code readers detected |
| Impact analysis | Per-class/method | Blast radius correct, no prefix confusion |
| Data flow | Per-endpoint | Endpoint → handler → downstream chain clean |

### Issues found and fixed during validation

Over 9 rounds of smoke testing, **25+ bugs** were discovered and fixed — each verified end-to-end on the real CortexFlow data before closing. Key categories:

- **FQN matching precision** — substring matching (`CONTAINS`) replaced with anchored matching to prevent prefix confusion between classes and their methods
- **Graph traversal direction** — bidirectional patterns caused sibling bleeding in class hierarchy; fixed with explicit directional queries
- **Kind resolution** — heuristic-based symbol type inference replaced with relational table lookup for authoritative `kind` values
- **Dead code false positives** — HTTP endpoints, event subscribers, and test methods excluded from candidates since they're invoked outside the C# call graph
- **Framework noise filtering** — System.*/Microsoft.*/CLR primitives filtered from callees/dependencies/data-flow output
- **Repository resolver** — stale duplicate repos silently hijacking queries; fixed with `LastIndexed` tie-breaking

## Running tests locally

```bash
# Unit tests only (fast, no Docker needed)
dotnet test tests/CortexPlexus.Core.Tests
dotnet test tests/CortexPlexus.Parsing.Tests
dotnet test tests/CortexPlexus.Search.Tests
dotnet test tests/CortexPlexus.Mcp.Tests
dotnet test tests/CortexPlexus.App.Tests
dotnet test tests/CortexPlexus.Embedding.Tests
dotnet test tests/CortexPlexus.Agent.Tests
dotnet test tests/CortexPlexus.Api.Tests

# Integration tests (requires Docker running)
dotnet test tests/CortexPlexus.Graph.Tests       # Apache AGE container
dotnet test tests/CortexPlexus.Integration.Tests  # pgvector container
```

## CI pipeline

Every push to `main` and every pull request triggers the full test suite via GitHub Actions. Test results are published as GitHub Check annotations — click any workflow run to see the per-test breakdown with pass/fail/skip counts and durations.

See: [Build & Test workflow](../.github/workflows/build-and-test.yml) | [Latest runs](https://github.com/DT-Tuan/CortexPlexus/actions/workflows/build-and-test.yml)
