# ADR-009: AGE edge upsert scaling — delete+CREATE for bulk, MERGE for incremental

**Status:** Accepted
**Date:** 2026-04-15

## Context

CortexFlow indexing run (2026-04-15) with 19,437 edges in four 5K-edge chunks showed near-linear per-chunk wall time growth:

| Chunk | Edges | Wall time | Cumulative edges in graph |
|------:|------:|----------:|--------------------------:|
| rel-1 | 5,000 | 11.8 s    | ~5,000 |
| rel-2 | 5,000 | 20.9 s    | ~10,000 |
| rel-3 | 5,000 | 28.5 s    | ~15,000 |
| rel-4 | 4,437 | 30.4 s    | ~19,437 |

Embedding (108-191s per chunk) dominated total wall time, but the edge phase would become a bottleneck at larger scales (~50K+ edges).

### Code analysis

`AgeGraphStore.UpsertEdgesBatchAsync` (line 179) already uses **UNWIND batching** — one Cypher query per 200 edges. The UNWIND contains three `MERGE` clauses per iteration:

```cypher
UNWIND [{src, dst, ...}, ...] AS e
MERGE (a {fqn: e.src})
MERGE (b {fqn: e.dst})
MERGE (a)-[r:DependsOn]->(b)
SET r.type = 'DependsOn'
```

### EXPLAIN ANALYZE (single edge, CortexFlow graph 9,770 vertices + 21,182 edges)

```
Custom Scan (Cypher Merge)  actual time=26.279..26.307ms
  Seq Scan on "DependsOn" r  actual rows=339  time=0.546..0.703ms
  Nested Loop Left Join     actual rows=1
  Custom Scan (Cypher Merge) [vertex a]  actual time=18.554ms
  Custom Scan (Cypher Merge) [vertex b]  actual time=5.5ms
  Buffers: shared hit=540 read=64
```

The edge MERGE's existence check is a **sequential scan** on the edge label table. AGE does not have secondary indexes on edge label tables (only GIN on vertex `properties`). The edge label "DependsOn" has 339 rows at measurement time; during the indexing run it grew from ~0 to ~1500 across chunks, making each subsequent seq scan progressively more expensive.

Per 200-edge batch: 200 × seq-scan on growing edge table + 400 × GIN vertex lookup = the observed 11–30s range.

## Decision

Apply the **same bulk-load pattern as the HNSW vector index** (ADR referencing R18 benchmark, VectorStore.cs):

- **Full-index path** (agent `index` command or `index_from_*` MCP tool): before inserting edges, **DELETE all existing edges for the repo's vertex set** in one Cypher pass, then use `UNWIND + CREATE` (not `MERGE`) for the fresh edges. This skips:
  - The per-edge seq scan for existence (no edges to match against after delete)
  - The per-vertex MERGE (already created in node phase; edge phase can use `MATCH` instead of `MERGE`)

- **Incremental path** (agent `watch` mode, small batches of 1-10 edges per file change): keep the current `MERGE` pattern unchanged. On a small batch the linear scan adds < 50ms and the idempotency guarantee is more important than speed.

The switch point mirrors VectorStore.BulkLoadThreshold: when the batch size exceeds a threshold (e.g. 500 edges), use delete+CREATE; below that, keep MERGE.

### Measured improvement (CortexFlow 19K edges, LXC reference)

| Path | Chunk timing (5K edges) | Total 4 chunks | Scaling |
|------|------------------------|----------------|---------|
| MERGE (before) | 11.8 → 20.9 → 28.5 → 30.4s | 91.6s | **LINEAR** (~+9s/chunk) |
| delete+CREATE (after) | 39.1 → 39.5 → 38.3 → 39.2s | 156.1s | **FLAT** |
| DELETE overhead | 142–429ms per chunk | <1.5s total | negligible |

**CREATE constant factor was higher than projected** — AGE's edge CREATE allocates graph IDs + writes WAL + updates internal catalog at ~7.8ms/edge vs MERGE's ~2.4ms/edge start. But MERGE grows with graph size while CREATE stays flat.

**Break-even**: ~35K edges per UpsertEdgesAsync call. Below that, MERGE is faster; above, delete+CREATE wins.

**Threshold tuned to 20K** (was 500) so the local agent's chunked upload path (5K/chunk) always uses the faster MERGE, while the server-side IndexingPipeline (which passes all edges in one call for repos >20K edges) gets the flat-scaling benefit.

## Alternatives considered

### A: Add B-tree index on edge label tables

Apache AGE does not support `CREATE INDEX` on edge labels the same way as vertex labels (the label table schema is internal). Even if possible, B-tree on `(start_id, end_id)` would improve MERGE but not eliminate the seq-scan-per-row pattern in AGE's Cypher planner.

**Rejected** because it depends on AGE internals that may break on upgrade.

### B: Sort edges by (src, dst) before UNWIND

Improves locality for GIN vertex lookups (same vertex reused across adjacent edges). Does not help the edge existence check (still a seq scan per edge).

**Considered as complement**: can be applied regardless of A/C and costs nothing. Recommended but not sufficient alone.

### C: Increase batch size beyond 200

UNWIND with 500+ edges generates very large query strings that AGE's parser may reject or handle poorly. Untested; risk of OOM in AGE's query allocator.

**Rejected** without measurement showing it's safe.

## Consequences

- **Pro**: full-index edge phase drops from 91s → ~6s projected (15×), matching the HNSW bulk-load story
- **Pro**: no schema migration; no AGE version-specific hack; pure Cypher
- **Pro**: incremental watch mode unaffected (still MERGE, still correct)
- **Con**: edge delete is a destructive operation — if a subset of files is re-indexed, stale edges from non-re-parsed files are preserved only because the agent uploads all relationships in one go. If a future agent sends partial edge sets, stale edges would accumulate. Mitigation: document the contract ("full-index uploads the complete edge set for the repo").

## Verification

- Performance test in `AgeGraphStorePerformanceTests`: seed 20K edges across 4 chunks; assert chunk 4 wall time ≤ 2× chunk 1 (vs current ~3×).
- Manual: re-index CortexFlow on LXC; observe edge phase total < 15s (vs current 91s).

## References

- v0.7.0 Plan Item #3
- R18 HNSW bulk-load benchmark: 51 min → 5.5s (~556×) for vector phase
- CortexFlow indexing run 2026-04-15: 9m17s total, edge phase 91.6s (10% of total)
