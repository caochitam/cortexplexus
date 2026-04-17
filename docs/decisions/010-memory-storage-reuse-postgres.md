# ADR-010: Memory storage reuses the existing PostgreSQL database

**Status:** Accepted
**Date:** 2026-04-17

## Context

v0.8.0 introduces a first-class agent memory system (see [MEMORY-SYSTEM.md](../MEMORY-SYSTEM.md)). It needs persistent storage for memory records with:

- Semantic retrieval (vector similarity on a 768-d embedding)
- Filter by scope, topic, and `related_fqns`
- Decay-weighted ordering at query time
- Cross-linking to `code_symbols` via FQN

CortexPlexus already runs a single PostgreSQL 17 instance with:

- pgvector (HNSW) for embedding search
- Apache AGE for the code graph
- tsvector / FTS for BM25
- A stateful, persistent `pgdata` volume (see [v0.7.0 data-persistence confirmation](../../docs/decisions/) — named volume survives upgrades)

The design choice: reuse this database or add a dedicated store (Redis, SQLite, a memory-specific service)?

## Decision

**Reuse the existing PostgreSQL database.** Memory lives in a new table `agent_memories` alongside `code_symbols`, `repositories`, and `file_hashes`.

The table gets:

- An HNSW index on `embedding vector(768)` for semantic recall
- A GIN index on `related_fqns text[]` for symbol-filtered queries
- A compound B-tree on `(scope, scope_id)` for list/filter operations

All memory operations go through the existing `NpgsqlDataSource` singleton. No new service, no new volume, no new port.

## Why

1. **Zero operational delta.** Users already run two containers (app + postgres). Adding a third (Redis, etc.) is deployment friction for no user-visible benefit at MVP scale.

2. **pgvector is already the right tool for the job.** Memory embeddings are the same shape (768-d from Gemini / Ollama) as `code_symbols.embedding`. Reusing pgvector means one index implementation, one correctness surface, one performance profile to tune.

3. **Symbol linking becomes free.** `related_fqns` in `agent_memories` references the same FQN format as `code_symbols.fqn`. A `JOIN` (or GIN-filtered query) lets us answer "memories linked to this symbol" without cross-service RPC.

4. **Backups are unified.** The existing `pgdata` volume backup story ([maintenance.md](../runbooks/maintenance.md)) covers memories for free. Users who dump `code_symbols` also dump `agent_memories`.

5. **Migration story is proven.** `Migrations.sql` is already idempotent and additive; adding a new `CREATE TABLE IF NOT EXISTS agent_memories` fits the existing pattern without breaking upgrades.

## Rejected alternatives

### Dedicated Redis

- Pros: fast single-key lookup, TTL is native.
- Cons: another service to deploy; no native vector search (RediSearch is an extension with separate licensing); adds a second source of truth; doesn't support semantic recall out of the box.
- **Why rejected**: Redis's ergonomic wins (native TTL, fast atomic ops) don't outweigh the deployment friction for a feature whose hot path is semantic search.

### Dedicated SQLite file

- Pros: no new container; per-project local file; trivial to deploy.
- Cons: no vector extension in mainline SQLite; pg_vector equivalent is a fork or a separate indexer; no concurrent multi-client safety if two agents race; can't reuse existing embedding pipeline.
- **Why rejected**: We'd end up rebuilding vector search primitives that pgvector already provides.

### Dedicated memory service (e.g. self-hosted Mem0 / Letta)

- Pros: feature-rich, battle-tested.
- Cons: adds deployment complexity equal to the PostgreSQL container we already have; licensing varies; they don't understand code symbols natively; we'd need a bridge layer anyway.
- **Why rejected**: Wrapping a foreign service loses CortexPlexus's differentiator (code-native retrieval). Better to build it ourselves as a thin layer on pgvector.

## Consequences

### Positive

- No new container. `docker compose pull && up -d` continues to work for v0.8.0.
- Memory and code-symbol queries can share transactions and indexes.
- Backup / restore / disaster recovery stories are identical to v0.7.x.

### Negative / risks

- **Table growth** — if a user stores thousands of memories, `agent_memories` grows alongside `code_symbols`. Mitigated by the Weibull decay reaper (ADR-012, Wave 2) and `MaxMemoriesPerScope` config ceiling.
- **Shared connection pool contention** — heavy memory queries compete with graph queries. Observed scale (~10–100 memories per active project) is well below the regime where this matters; revisit if a single project exceeds 10K.
- **No multi-tenant isolation** — any client with DB access sees all memories. This matches the existing model for `code_symbols`; not a regression.

## Follow-ups

- Monitor table size in `list_repositories` Health output.
- If a user reports >100K memories in a single project, revisit partitioning (by scope or by created_at month).
- Future consideration: a read-replica topology if recall traffic grows. Not needed for v0.8.0.
