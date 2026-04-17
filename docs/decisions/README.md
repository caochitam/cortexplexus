# Architecture Decision Records

| # | Decision | Status | Date |
|---|----------|--------|------|
| [001](001-postgresql-unified-store.md) | PostgreSQL unified store (AGE + pgvector + tsvector) | Accepted | 2026-04-03 |
| [002](002-monolith-architecture.md) | Monolith architecture (single .NET app) | Accepted | 2026-04-03 |
| [003](003-roslyn-over-treesitter-csharp.md) | Roslyn for C# instead of Tree-sitter | Accepted | 2026-04-03 |
| [004](004-google-gemini-embedding.md) | Google Gemini Embedding (free tier) as default | Accepted | 2026-04-03 |
| [005](005-mcp-dual-transport.md) | MCP dual transport (stdio + HTTP) | Accepted | 2026-04-03 |
| [008](008-kind-aware-health-metric.md) | Kind-aware Health metric | Accepted | 2026-04-15 |
| [009](009-age-edge-upsert-scaling.md) | AGE edge upsert: delete+CREATE for bulk, MERGE for incremental | Accepted | 2026-04-15 |
| [010](010-memory-storage-reuse-postgres.md) | Memory storage reuses existing PostgreSQL | Accepted | 2026-04-17 |
| [011](011-memory-scope-model.md) | Memory scope: session / project / global | Accepted | 2026-04-17 |
| [013](013-memory-opt-in-default.md) | Memory system opt-in, default disabled | Accepted | 2026-04-17 |
