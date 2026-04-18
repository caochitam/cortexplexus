# CortexPlexus — Architecture

## 1. System Overview

CortexPlexus is a monolith .NET application that:
1. **Parses** source code (Roslyn for C#, Tree-sitter for others)
2. **Builds** a Knowledge Graph in PostgreSQL (Apache AGE)
3. **Indexes** embeddings for semantic search (pgvector + Google Gemini)
4. **Serves** structured context to AI assistants via MCP

**External dependency:** PostgreSQL only. Everything else runs in-process.

## 2. High-Level Architecture

```
┌────────────────────────────────────────────────────────────────┐
│                      IDE / AI Agent                             │
│   VS Code  ·  Cursor  ·  Claude Code  ·  Visual Studio         │
│                    MCP Protocol (HTTP /mcp)                     │
└────────────────────────────┬───────────────────────────────────┘
                             │
┌────────────────────────────┼───────────────────────────────────┐
│                     Web Browser                                 │
│              Graph Explorer (Cytoscape.js)                      │
│                    REST API (/api/*)                            │
└────────────────────────────┬───────────────────────────────────┘
                             │
┌────────────────────────────┴───────────────────────────────────┐
│                                                                 │
│                CortexPlexus.App  (Single Process)               │
│                                                                 │
│   ┌─────────────────────────────────────────────────────────┐  │
│   │                    CLI Router                            │  │
│   │   init  ·  index  ·  serve  ·  status  ·  search        │  │
│   └────────────────────────┬────────────────────────────────┘  │
│                            │                                    │
│   ┌─────────────────────────────────────────────────────────┐  │
│   │ Web UI + REST API                                        │  │
│   │   /              → Static files (Cytoscape.js SPA)       │  │
│   │   /api/*         → GraphApiEndpoints (MinimalAPIs)       │  │
│   │   /mcp           → MCP Streamable HTTP transport         │  │
│   └─────────────────────────────────────────────────────────┘  │
│                            │                                    │
│   ┌────────────┐  ┌───────┴───────┐  ┌─────────────────────┐  │
│   │ MCP Server │  │ Indexing      │  │ File Watcher        │  │
│   │            │  │ Pipeline      │  │                     │  │
│   │ Tools:     │  │               │  │ FileSystemWatcher   │  │
│   │ -search    │  │ ┌───────────┐ │  │ → detect changes    │  │
│   │ -callers   │  │ │ Roslyn    │ │  │ → enqueue to        │  │
│   │ -deps      │  │ │ Parser    │ │  │   Channel<T>        │  │
│   │ -impact    │  │ ├───────────┤ │  └─────────────────────┘  │
│   │ -etc.      │  │ │ Secrets   │ │                            │
│   │            │  │ │ Scanner   │ │                            │
│   │ Query:     │  │ ├───────────┤ │                            │
│   │ ┌────────┐ │  │ │ Embedding │ │                            │
│   │ │ Hybrid │ │  │ │ Service   │ │                            │
│   │ │ Router │ │  │ ├───────────┤ │                            │
│   │ │        │ │  │ │ Graph     │ │                            │
│   │ │ Query  │ │  │ │ Writer    │ │                            │
│   │ │Expander│ │  │ └───────────┘ │                            │
│   │ │(Ollama)│ │  └───────────────┘                            │
│   │ │  ↓     │ │                                               │
│   │ │ Graph  │ │                                               │
│   │ │ Vector │ │                                               │
│   │ │ BM25   │ │                                               │
│   │ │  ↓     │ │                                               │
│   │ │ RRF    │ │                                               │
│   │ │  ↓     │ │                                               │
│   │ │Context │ │                                               │
│   │ │Compress│ │                                               │
│   │ └────────┘ │                                               │
│   └────────────┘                                               │
│                                                                 │
└────────────────────────────┬───────────────────────────────────┘
                             │  Npgsql
                             ▼
┌────────────────────────────────────────────────────────────────┐
│                    PostgreSQL 17+                                │
│                                                                 │
│   ┌────────────────┐  ┌──────────────┐  ┌──────────────────┐  │
│   │  Apache AGE    │  │  pgvector    │  │  tsvector        │  │
│   │                │  │              │  │                  │  │
│   │  code_graph    │  │  code_symbols│  │  code_symbols    │  │
│   │  (nodes+edges) │  │  .embedding  │  │  .search_text    │  │
│   │                │  │              │  │                  │  │
│   │  Cypher queries│  │  HNSW index  │  │  GIN index       │  │
│   └────────────────┘  └──────────────┘  └──────────────────┘  │
│                                                                 │
└────────────────────────────────────────────────────────────────┘
```

## 3. Component Details

### 3.1 CortexPlexus.Core
**Responsibility:** Domain models + abstractions shared by all modules.

```
Core/
├── Models/
│   ├── CodeSymbol.cs          # Base record for all code symbols
│   ├── ClassInfo.cs           # Class/struct/record metadata
│   ├── MethodInfo.cs          # Method metadata + signature
│   ├── InterfaceInfo.cs
│   ├── PropertyInfo.cs
│   ├── Relationship.cs        # Edge types (CALLS, IMPLEMENTS, etc.)
│   └── IndexingJob.cs         # Job queue payload
│
└── Abstractions/
    ├── ICodeParser.cs          # Parse source → list of symbols + relationships
    ├── IGraphStore.cs          # Write/read graph (AGE)
    ├── IVectorStore.cs         # Write/read embeddings (pgvector)
    ├── IFullTextStore.cs       # Write/read FTS (tsvector)
    ├── IEmbeddingService.cs    # Generate embeddings
    ├── IQueryExpander.cs       # HyDE + multi-query expansion (Phase 5)
    └── ISecretsScanner.cs      # Strip sensitive data
```

### 3.2 CortexPlexus.Parsing
**Responsibility:** Extract code intelligence from source files.

**Roslyn pipeline:**
```
.sln/.csproj → MSBuildWorkspace → Compilation → SemanticModel
    → CSharpSyntaxWalker subclasses:
        ClassVisitor      → ClassInfo nodes
        MethodVisitor     → MethodInfo nodes + CALLS edges
        TypeUsageVisitor  → USES_TYPE, CREATES edges
        InheritanceVisitor → INHERITS, IMPLEMENTS edges
```

**Key design:** Uses `SymbolEqualityComparer.Default` for all ISymbol-keyed collections (Roslyn symbols are reference-unequal even when logically identical).

### 3.3 CortexPlexus.Graph
**Responsibility:** All PostgreSQL interactions (AGE + pgvector + tsvector).

**Three stores, one database:**
- `AgeGraphStore` — Cypher queries via `SELECT * FROM cypher(...)`
- `VectorStore` — pgvector operations (insert embeddings, similarity search)
- `FullTextStore` — tsvector operations (insert search text, full-text query)

**Write pattern:** Batch insert via parameterized SQL + UNWIND for performance.

### 3.4 CortexPlexus.Search
**Responsibility:** Hybrid search engine with optional LLM-powered query expansion.

```
Query → QueryClassifier → route to search engine(s)
    │
    ├── [Optional] QueryExpander (HyDE + Multi-query via Ollama)
    │   ├── HyDE: generate hypothetical answer → embed → vector search
    │   └── Multi-query: generate 3 variants → BM25 search each
    │
    ├── GraphSearch (AGE Cypher) ──┐
    ├── VectorSearch (pgvector) ───┤── RRF Fusion ── ContextCompressor ── Result
    ├── Bm25Search (tsvector) ─────┤
    └── ExpandedSearch (variants) ─┘
```

**Query Expansion (Phase 5):**
- `IQueryExpander` interface with two implementations:
  - `OllamaQueryExpander` — uses Ollama `/api/generate` for HyDE + multi-query
  - `NoOpQueryExpander` — pass-through when expansion disabled
- Activated per-request via `SearchRequest.Expand = true` flag
- Graceful fallback: if Ollama unavailable → original query used

**Reciprocal Rank Fusion:**
```
RRF_score(d) = Σ 1/(k + rank_i(d))    where k = 60
Sources: vector, bm25, expanded (when query expansion enabled)
```

**Weighted Full-Text Search:**
```sql
-- tsvector weights: A=name(1.0), B=fqn(0.4), C=signature(0.2), D=unused(0.1)
ts_rank('{0.1, 0.2, 0.4, 1.0}', search_text, query)
```

**Context compression levels:**
| Level | Content | ~Tokens/method |
|-------|---------|----------------|
| L0 | Name + signature only | 50 |
| L1 | L0 + AI summary | 150 |
| L2 | L1 + relationships + branches | 500 |
| L3 | Full source code (read from file) | Unlimited |

### 3.5 CortexPlexus.Embedding
**Responsibility:** Generate vector embeddings for code symbols.

**Providers:**
- `GeminiEmbeddingService` — Google Gemini API (default, free tier)
- `OllamaEmbeddingService` — Ollama local (offline fallback)

**Strategy:** Embed `signature + summary` (not full body). Reduce dimensions to 768 for storage.

**Embeddable kinds (intentional subset of all parsed symbols):**

```
class · method · interface · struct · record · function · type · document · section
```

Other kinds (`field`, `property`, `event`, `constructor`, `enum`, `enum_member`, `parameter`, `namespace`, `di_registration`, `api_endpoint`, `middleware`, `config_key`, `dbcontext`) are persisted to `code_symbols` with `embedding IS NULL` — they are reachable via graph traversal and full-text search but skip semantic search by design. Embedding low-signal kinds (a property accessor, a constructor) adds noise without unlocking new questions.

The implementation reference is the `embeddable` filter in `IndexingPipeline.IndexAsync`. The same enum drives the kind-aware [Health metric](HEALTH-METRICS.md) shown by `list_repositories`. If you change the filter, update both `HEALTH-METRICS.md` and the constant referenced by `GraphTraversalTools.cs` in the same PR — they are intentionally a single source of truth.

### 3.6 CortexPlexus.App
**Responsibility:** Monolith entry point. Hosts Web UI + MCP Server + REST API + Background Indexer + CLI.

**Startup flow (serve command):**
```csharp
var app = builder.Build();

// 1. Static files (Cytoscape.js graph explorer SPA)
app.UseStaticFiles();
app.MapGet("/", () => Results.Redirect("/index.html"));

// 2. REST API (graph visualization endpoints)
app.MapGraphApi();  // /api/repositories, /api/graph/{repoId}, /api/graph/node, /api/search

// 3. MCP Server (AI agent protocol)
app.MapMcp("/mcp");  // Streamable HTTP transport

// Background services
app.Services.AddHostedService<IndexingWorker>();   // always
app.Services.AddHostedService<FileWatcherService>(); // if --watch
```

**Web Frontend (Phase 6):**
- `wwwroot/index.html` — SPA shell with toolbar, graph canvas, sidebar
- `wwwroot/js/app.js` — Cytoscape.js graph explorer (~300 lines)
- `wwwroot/css/app.css` — Dark theme, responsive layout
- Loads Cytoscape.js from CDN, zero build tools

**REST API (`Api/GraphApiEndpoints.cs`):**
| Method | Route | Handler |
|--------|-------|---------|
| GET | `/api/repositories` | List all indexed repos |
| GET | `/api/graph/{repoId}` | Graph overview (nodes + edges) |
| GET | `/api/graph/node?fqn=` | Node neighbors expansion |
| GET | `/api/search?q=` | Hybrid search |

## 4. Data Flow Diagrams

### 4.1 Indexing Flow

```
File Change
    │
    ▼
FileSystemWatcher (or CLI trigger)
    │
    ▼
Channel<IndexingJob>           ◄── In-process queue
    │
    ▼
IndexingWorker (BackgroundService)
    │
    ├── 1. Check content hash → skip if unchanged
    ├── 2. Roslyn parse → symbols + relationships
    ├── 3. Secrets scan → strip credentials
    ├── 4. Embedding → Gemini API (batch)
    └── 5. Write to PostgreSQL
            ├── AgeGraphStore.UpsertNodes()
            ├── AgeGraphStore.UpsertEdges()
            ├── VectorStore.UpsertEmbeddings()
            └── FullTextStore.UpsertSearchText()
```

### 4.2 Query Flow

```
MCP Tool Call (e.g., semantic_search "payment", expand=true)
    │
    ▼
MCP Server (Tool Handler)
    │
    ▼
HybridQueryRouter
    │
    ├── Classify query type
    │   ├── Structural? → Graph-first
    │   ├── Semantic? → Vector-first
    │   ├── Exact name? → BM25-first
    │   └── Ambiguous? → All three (Hybrid)
    │
    ├── [If expand=true && IQueryExpander.IsEnabled]
    │   ├── HyDE: query → Ollama generate hypothetical → embed → vector search
    │   └── Multi-query: query → Ollama generate 3 variants → BM25 each
    │
    ├── Execute parallel searches
    │   ├── AgeGraphStore.Query()
    │   ├── VectorStore.Search() ← uses HyDE embedding if expanded
    │   ├── FullTextStore.Search() (weighted: name=A, fqn=B, sig=C)
    │   └── FullTextStore.Search() × N (multi-query variants)
    │
    ├── RRF Fusion (merge vector + bm25 + expanded → rank)
    │
    ├── ContextCompressor (L0→L3 auto-select)
    │
    └── Return MCP response
```

## 5. Database Schema

### 5.1 Graph (Apache AGE)
```
Graph: code_graph

Node labels: Repository, Project, Namespace, Class, Interface,
             Method, Property, Constructor, ApiEndpoint,
             DbContext, DbSet, DIRegistration, NuGetPackage, Module

Edge labels: CONTAINS_PROJECT, CONTAINS_NAMESPACE, DECLARES,
             INHERITS, IMPLEMENTS, HAS_METHOD, HAS_PROPERTY,
             CALLS, CREATES, USES_TYPE, OVERRIDES,
             HANDLED_BY, HTTP_CALLS, MAPS_TO, DEPENDS_ON,
             REFERENCES, BELONGS_TO_MODULE
```

### 5.2 Relational (pgvector + tsvector)
```sql
code_symbols (
    id          BIGSERIAL PRIMARY KEY,
    fqn         TEXT UNIQUE NOT NULL,
    name        TEXT NOT NULL,
    kind        TEXT NOT NULL,
    signature   TEXT,
    file_path   TEXT,
    start_line  INT,
    end_line    INT,
    repo_id     UUID NOT NULL,
    embedding   vector(768),
    search_text tsvector GENERATED ALWAYS AS (
        setweight(to_tsvector('english', name), 'A') ||
        setweight(to_tsvector('english', fqn), 'B') ||
        setweight(to_tsvector('english', signature), 'C')
    ) STORED
)

-- Indexes: HNSW (vector), GIN (tsvector), B-tree (fqn, repo_id)
```

### 5.5 Agent Memory Store (v0.8.0+, opt-in)

Fourth pillar alongside graph, vector, and FTS — but gated behind an opt-in flag (ADR-013, default `Memory.Enabled=false`). Agents call 4 MCP tools (save/recall/list/forget) to persist scoped, decay-weighted memories across sessions.

```sql
CREATE TABLE agent_memories (
    id               UUID        PRIMARY KEY DEFAULT gen_random_uuid(),
    content          TEXT        NOT NULL,    -- 1..4000 chars (CHECK)
    scope            TEXT        NOT NULL,    -- 'session' | 'project' | 'global'
    scope_id         TEXT        NULL,        -- required unless scope='global'
    topic            TEXT        NULL,        -- 'preference'|'pattern'|'decision'|'bug'|'todo'|'note'
    importance       REAL        NOT NULL DEFAULT 0.5,  -- 0..1 (CHECK)
    related_fqns     TEXT[]      NOT NULL DEFAULT ARRAY[]::TEXT[],  -- soft link to code_symbols.fqn
    embedding        vector(768) NULL,        -- same model as code_symbols
    created_at       TIMESTAMPTZ NOT NULL,
    last_accessed_at TIMESTAMPTZ NOT NULL,
    access_count     INT         NOT NULL DEFAULT 0
)

-- Indexes: HNSW (embedding), GIN (related_fqns), B-tree (scope, scope_id)
```

Recall ranking combines semantic similarity (cosine) with a **Weibull decay** score (ADR-012): `score = importance × exp(-(t/λ)^1.5)`, where λ is per-topic (30–365 days). The `MemoryReaper` background service deletes rows whose score falls below 0.1 on a configurable interval.

Soft link to the symbol graph: `related_fqns` is a denormalised text[] (GIN-indexed), not a foreign key, because FQNs drift across rename refactors. `get_impact_analysis` and `explore_topic` accept an `include_memories=true` flag to surface memories linked to the symbol under investigation alongside the normal impact/explore report.

See [MEMORY-SYSTEM.md](MEMORY-SYSTEM.md) for the full spec, [ADR-010](decisions/010-memory-storage-reuse-postgres.md) for the Postgres-reuse rationale, [ADR-011](decisions/011-memory-scope-model.md) for the scope model, [ADR-012](decisions/012-memory-decay-weibull.md) for the decay curve, and [ADR-013](decisions/013-memory-opt-in-default.md) for the opt-in default.

## 6. Security Model

```
Source Code (local filesystem)
    │
    ├── Roslyn reads file ──────────── stays local
    │
    ├── Extract metadata ───────────── fqn, signature, line numbers
    │   (NO method bodies stored)       → written to PostgreSQL
    │
    ├── Secrets Scanner ────────────── strips credentials
    │
    └── Embedding Service ──────────── sends ONLY signature+summary
        │                               to Google Gemini API
        └── Returns vector ─────────── stored in pgvector
```

## 7. Deployment Architecture

```
Developer Machine
│
├── Docker Compose
│   ├── postgres (apache/age:latest)     Port 5432
│   │   └── Extensions: age, vector
│   │
│   └── cortexplexus (.NET 10 app)       Port 8080 (HTTP)
│       ├── /              Web UI (Graph Explorer)
│       ├── /api/*         REST API (graph data)
│       ├── /mcp           MCP Streamable HTTP
│       └── Volumes:
│           ├── /workspace:ro             (mounted code)
│           └── /app/data                 (logs, config)
│
├── Browser connects to:
│   └── http://localhost:8080/            (Graph Explorer)
│
└── IDE connects via:
    ├── stdio  (default, zero-config)
    └── HTTP   (http://localhost:8080/mcp)
```
