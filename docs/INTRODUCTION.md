# CortexPlexus — Turn your source code into a Knowledge Graph for AI

**Open-source Code Intelligence Platform. 100% self-hosted, free forever.**

AI coding assistants (Claude, Cursor, Copilot) read your codebase as plain text — they don't understand the **structure** of your code. The result: agents `grep` and `read` dozens of files to infer class/method relationships, wasting tokens, missing context, giving confidently wrong answers.

**CortexPlexus fixes that.** It parses your code with Roslyn (C# deep semantic) + Tree-sitter (TypeScript, JavaScript, Python, Java, Go, Rust, PHP), builds a Knowledge Graph (classes, methods, call graph, DI wiring, API routes, EF Core mappings, test coverage, config usage…), and serves it to any AI agent over the **Model Context Protocol** — **1 tool call instead of 10+ grep/read operations**.

---

## Core value — measurable, not marketing fluff

| Before CortexPlexus | With CortexPlexus | Benefit |
|---|---|---|
| Grep class name → read 5-10 files → manually assemble | `GetCallers("Method.FQN")` | **1 call replaces 10+** |
| Understand a service: read class + tests + deps (15+ files) | `ExploreTopic("ServiceName")` | **1 call replaces 15+** |
| Trace an API request: Program.cs → handler → downstream | `GetDataFlow("/api/orders")` | **1 call replaces 8+** |
| Impact analysis for refactor: grep → read callers → read caller-of-callers | `GetImpactAnalysis(method, depth: 3)` | **1 call replaces 10+** |
| Onboard new project: read 20+ files manually | `OnboardProject(repo)` | **1 call replaces 20+** |

**Real-world impact for AI agents:**
- 80-90% reduction in token/inference cost
- Accurate answers — agent works from structured context, not guesses based on snippets
- New-project onboarding in under 30 seconds

---

## 26 MCP tools — covering four real needs

**Search & navigation** — `search_code` (hybrid full-text + vector), `semantic_search` (natural-language), `get_callers` / `get_callees`, `get_implementations` (interface → class), `get_class_hierarchy` (directional, no sibling bleeding), `get_dependencies`, `get_impact_analysis`.

**.NET deep analysis** — `get_di_registrations` (service → implementation), `get_entity_mapping` (DbContext → entity), `get_api_endpoints` (with `[controller]` token expansion), `get_data_flow` (endpoint → handler → DB), `get_middleware_pipeline` (ASP.NET execution order), `get_nuget_audit`, `get_architecture`.

**Quality & observability** — `get_test_coverage` (8 frameworks: xUnit, NUnit, pytest, Jest, JUnit, Go test, Rust cargo, PHPUnit), `get_config_usage` (detects `appsettings.json` / `.env` / `IConfiguration` / `IOptions<T>` / env-var APIs), `get_dead_code` (filters HTTP endpoints, event subscribers, test methods), `get_circular_dependencies` (DFS on `DependsOn` graph).

**Composite** — `explore_topic` (search + callers + deps + implementations, one call), `onboard_project` (whole-project overview, one call).

---

## Measured performance on real projects

**R18 — HNSW bulk-load (benchmarked on pgvector + pg17):**
> Vector indexing phase: **51 minutes → 5.5 seconds (~556× speedup)** for batches ≥500 symbols.
> Strategy: drop HNSW → bulk INSERT → rebuild HNSW, instead of paying per-row HNSW maintenance.

**Scale test (CortexFlow — real full-stack .NET solution):**
> 11,633 symbols / 97,117 relationships / 8,399 embeddings indexed in ~30 minutes.
> Bottleneck is Ollama single-thread (25-30s/batch); with Gemini API free tier, wall time drops 3-4×.

**Search quality (hybrid fusion):**
> Apache AGE Cypher (graph) + pgvector HNSW (vector, ef_search=100 for ~99% recall) + tsvector BM25 (full-text), fused via Reciprocal Rank Fusion.

**Incremental indexing:**
> SHA-256 content hash + file watcher → only re-index files that changed. Edit-to-reindex loop < 1 second per file.

---

## Six practical use cases

1. **Onboard a new project** — agent has a full architectural map on first connect, no need to "learn" by reading README + wandering files.
2. **Debug a complex bug** — `get_data_flow("/api/failing-endpoint")` returns the full handler → service → repository → DB chain, pinpointing the stage that needs a breakpoint.
3. **Pre-merge impact analysis** — `get_impact_analysis(method, depth: 3)` lists exactly how many callers will break if a signature changes.
4. **Test-coverage audit** — find tests for any production method; surface untested hot paths in CI.
5. **Codebase cleanup** — `get_dead_code` + `get_circular_dependencies` surface removable zones in one call; replaces expensive standalone tools (NDepend, SonarQube).
6. **API governance** — `get_api_endpoints` + `get_middleware_pipeline` to review security policy before release.

---

## Why you can trust it

| Criteria | CortexPlexus |
|---|---|
| **Tests** | 693 passing (unit + integration + performance), ~85% coverage |
| **Languages** | 8 (deep C# via Roslyn, 7 others via Tree-sitter) |
| **Deployment** | 2 Docker containers, < 2 GB RAM, < 2 GB disk |
| **License** | MIT (commercial use, fork, rebrand — all free) |
| **External dependencies** | Zero (Ollama offline is the default; Gemini free tier is optional) |
| **DB stack** | 1 PostgreSQL 17 + AGE 1.6 + pgvector 0.8.2 + tsvector — no Redis, RabbitMQ, Elasticsearch |

---

## Quick comparison with alternatives

| | GitHub Copilot | Cursor search | Sourcegraph | **CortexPlexus** |
|---|:---:|:---:|:---:|:---:|
| Open source | No | No | Partial | **Yes (MIT)** |
| Self-hosted | No | No | Yes (Enterprise) | **Yes (free)** |
| Roslyn deep C# | No | No | No | **Yes** |
| Knowledge Graph (beyond text) | No | No | Yes | **Yes** |
| MCP native | No | Partial | No | **Yes (26 tools)** |
| Annual cost / 20 devs | ~$5,000 | ~$5,000 | $10,000+ | **$0** |

---

## Get started in 3 commands

```bash
git clone https://github.com/DT-Tuan/CortexPlexus.git
cd cortexplexus
docker compose up -d
```

Drop a `.mcp.json` at your project root pointing at `http://localhost:8080/mcp`, restart your IDE, and your agent gets 26 code-intelligence tools. Your source code stays on your machine — the Local Agent only uploads metadata.

---

## Who it's for

- **C# / .NET teams** that want their AI agent to understand enterprise codebases (DI containers, EF Core, middleware stacks) without paying a SaaS subscription.
- **Tech leads** who need CI-integrated impact analysis / dead code / test-coverage audits without buying $10K/year tools.
- **Solo devs and startups** who want Copilot-Enterprise-grade intelligence on their own machine — offline, privacy-first.
- **Researchers and OSS contributors** looking for a platform to experiment with RAG-on-code, Knowledge Graphs, and hybrid search.

---

**Repo**: https://github.com/DT-Tuan/CortexPlexus · **License**: MIT · **Stack**: .NET 10 + PostgreSQL 17 (AGE + pgvector + tsvector) + Roslyn + Tree-sitter + Ollama/Gemini embeddings + ModelContextProtocol SDK.

> Also available in Vietnamese: [INTRODUCTION-VI.md](./INTRODUCTION-VI.md).
