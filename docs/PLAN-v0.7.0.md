# Plan — v0.7.0 (Indexing UX & Health Accuracy)

> **Status**: draft. Discussed but not yet started. Each item lists the doc to update first, the code change after, and the acceptance criteria to close it.

## Context

Surfaced during the v0.6.0 verification run on 2026-04-15: a single `dotnet cortexplexus-agent index` against the CortexFlow .NET solution (410 .cs files → 5,273 unique symbols → 19,437 relationships → 2,130 embeddings, total 9m17s). Three issues observed; one is user-visible-misleading, one is naming-misleading, one is performance-scaling.

This file is the working spec — when an item is implemented, this file's section moves into the matching CHANGELOG entry under `[Unreleased]` and links back to the commit/PR.

---

## Doc-first principle (guardrails for this milestone)

For every item below:

1. **Doc first** — write the new behavior, schema, or threshold into the relevant doc (`ARCHITECTURE.md`, `HEALTH-METRICS.md`, ADR, etc.) **before** writing code. The doc becomes the spec.
2. **Test second** — capture the expected behavior in a failing test referencing the doc paragraph.
3. **Implement third** — code the change until the test passes.
4. **CHANGELOG fourth** — add a bullet under `[Unreleased]` per `CONTRIBUTING.md` rule. Cross-link to the doc.
5. **Verify on LXC fifth** — re-index a real repo, eyeball the output against the doc.

If a code change ships without the doc, reviewer rejects.

---

## Item #1 — Health threshold should be kind-aware (P1)

**Reproduction**

```
ListRepositories()
→  CortexFlow      Health: PARTIAL — 2130/5273 (40%)  ← misleading
   CortexPlexus    Health: PARTIAL — 76/95 (80%)
   TaskSchedulerApp Health: PARTIAL — 100/137 (73%)
```

All three repos are healthy. The `40%` looks alarming but is by design: only `class | method | interface | struct | record | function | type | document | section` are embeddable; `field | property | event | constructor` are intentionally skipped. The current threshold (`90%`) compares `embeddings_count / total_symbols`, so every healthy .NET repo (where 50–70% of symbols are non-embeddable kinds) shows `PARTIAL`.

**Doc-first deliverables** (write these BEFORE touching code)

1. **NEW [`docs/HEALTH-METRICS.md`](HEALTH-METRICS.md)** — explains what each Health label means, what kinds embed vs don't, and how the ratio is computed. Reference list of embeddable kinds (single source of truth — `IndexingPipeline.cs:139` becomes the implementation of this doc, not a separate fact).
2. **NEW [`docs/decisions/008-kind-aware-health-metric.md`](decisions/008-kind-aware-health-metric.md)** — ADR: why we compare against embeddable kinds rather than total symbols, why 90% is the right threshold against that denominator, why we don't just lower the threshold.
3. **`README.md` Features section** — one-line clarification of what `Health: OK` means.
4. **`docs/MCP-GUIDE.md` `list_repositories` section** — link to HEALTH-METRICS.md.

**Code change**

- `src/CortexPlexus.App/Mcp/Tools/GraphTraversalTools.cs` — change the Postgres query to count embeddable symbols separately:
  ```sql
  SELECT repo_id,
         COUNT(*) FILTER (WHERE kind IN ('class','method','interface','struct',
                                          'record','function','type','document','section')) AS embeddable,
         COUNT(*) FILTER (WHERE embedding IS NOT NULL) AS with_embedding,
         COUNT(*) AS total
  FROM code_symbols
  GROUP BY repo_id;
  ```
- Health rule:
  ```
  embeddable == 0                 → "OK — no embeddable symbols (e.g. config-only repo)"
  with_embedding / embeddable >= 0.9 → "OK — N symbols, M with embeddings (P% of embeddable)"
  with_embedding / embeddable >= 0.5 → "PARTIAL"
  with_embedding == 0 && embeddable > 0 → "DEGRADED"
  ```
- Output line should always show **both** numbers ("76 of 95 embeddable symbols, 80%") so users see the calculus.

**Test**

- New unit test in `CortexPlexus.Mcp.Tests` mocking `IRepositoryStore` AND a fake `NpgsqlDataSource` that returns rows with mixed kinds; assert label is "OK" not "PARTIAL".
- Integration test (Graph.Tests with AgeFixture + pgvector container) that seeds class/method/property symbols and checks the rendered Health line.

**Acceptance**

- After re-running `ListRepositories()` against the current LXC state, all three existing repos show `OK` (not PARTIAL), with the embedding-coverage number reflecting embeddable kinds only.
- `docs/HEALTH-METRICS.md` is link-reachable from the README and the MCP-GUIDE.
- ADR 008 explains the choice with two paragraphs of why-not-alternatives.

**Scope estimate**: ~2 hours including docs + ADR + tests.

---

## Item #2 — `IndexResultsResponse.EmbeddingsPersisted` is misnamed (P2, breaking)

**Reproduction**

A chunk with 2,000 symbols (1,860 unique after dedup, 479 embeddable) returns:

```json
{
  "symbols": 2000,
  "embeddings": 479,
  "embeddingsPersisted": 1860,   ← actually symbol-row insert count
  "embeddingsFailed": 0
}
```

The client (LocalIndexer) checks `EmbeddingsFailed > 0` and throws on positive — that logic stays correct because the failure path *is* batch-level. But the `Persisted` value is inscrutable: it doesn't equal `embeddings`, doesn't equal `symbols`, looks like a bug to anyone parsing the JSON.

**Doc-first deliverables**

1. **NEW [`docs/API.md`](API.md)** — concise schema reference for `/api/agent/*` endpoints. Define every response field, its semantic, and whether it includes nulls. Becomes the spec for the rename.
2. **`CHANGELOG.md`** — under `[Unreleased] → Breaking`, documents the field rename + the wire-compat plan.
3. **`docs/ARCHITECTURE.md`** — short note clarifying that the vector store inserts a row for every symbol but the `embedding` column is nullable.

**Code change**

- `src/CortexPlexus.App/Api/Dto/IndexResultsDto.cs` — split into two fields:
  ```csharp
  public required int SymbolsPersisted { get; init; }   // was EmbeddingsPersisted
  public required int SymbolsFailed { get; init; }      // was EmbeddingsFailed
  public required int VectorRowsWritten { get; init; }  // NEW: actual embedding column non-null
  ```
- `src/CortexPlexus.Graph/VectorStore.cs` — extend `VectorUpsertResult` to also count vector rows.
- `src/CortexPlexus.Agent/LocalIndexer.cs.UploadAck` — match the new schema; keep parsing the old field name as a fallback for one release ("if `SymbolsFailed` missing, fall back to `EmbeddingsFailed`").

**Wire-compat strategy**

- v0.7.0 server: emits new fields. Old (1.1.0) agents still see `EmbeddingsFailed` for one release because we keep emitting it as an alias (deprecated).
- v0.8.0: drop the alias. CHANGELOG flags the removal.

**Test**

- `CortexPlexus.App.Tests` — assert response body has the new fields with correct values.
- `CortexPlexus.Agent.Tests` — assert UploadAck record parses both old + new field names.

**Acceptance**

- Both 1.1.0 and 1.2.0 agents work against a v0.7.0 server.
- New API.md page is at least the single source of truth for `/api/agent/version` + `/api/agent/download` + `/api/index/results` schemas.

**Scope estimate**: ~3 hours including the alias compat layer + tests + API.md skeleton.

---

## Item #3 — AGE edge upsert scales near-linearly per chunk (P3, investigation)

**Reproduction**

CortexFlow indexing, 5,000-edge chunks back-to-back:

| chunk | edges | wall time |
|------:|------:|---------:|
| rel-1 | 5,000 | 11.8 s   |
| rel-2 | 5,000 | 20.9 s   |
| rel-3 | 5,000 | 28.5 s   |
| rel-4 | 4,437 | 30.4 s   |

Each chunk is ~9 s slower than the previous. For a hypothetical 50K-edge repo, chunk 10 would be ~90 s; chunk 20 would push 3 minutes. Embeddings phase already dominates today, but at large scale the AGE edge phase can compete.

**Investigation (doc-first)**

Before any code, write **[`docs/decisions/009-age-edge-upsert-scaling.md`](decisions/009-age-edge-upsert-scaling.md)** capturing:
- Hypotheses (vlabel index growth? Cypher MATCH-CREATE pattern? per-edge property GIN re-balance? AGE catalog lock contention?)
- Measurement plan (EXPLAIN ANALYZE on a 5K-edge UNWIND, then the same after 50K edges; pg_stat_statements top-N by call count)
- Decision criteria for adopting / rejecting bulk-load pattern (mirroring the HNSW drop-and-recreate trick from R18)

**Code change** — only AFTER the ADR is signed off:

Possible candidates (TBD by measurement):
- Drop AGE edge indexes before bulk insert, recreate after.
- Sort edges by `(label, fromFqn)` to improve B-tree insert locality.
- Pre-batch edges by label so each Cypher statement only touches one edge type.

**Test**

- `CortexPlexus.Graph.Tests` performance test (existing `AgeGraphStorePerformanceTests`): seed 50K edges across 5 chunks; assert chunk N is within 2× of chunk 1 (vs the ~3× we see today).

**Acceptance**

- ADR 009 published with at least one EXPLAIN ANALYZE attached.
- If ADR concludes "no fix worth shipping", the doc still ships and explains why.
- If a fix ships, the perf test enforces the new bound.

**Scope estimate**: ~half a day for investigation + ADR; another half day if a fix is warranted.

---

## Cross-cutting docs (write while doing #1 & #2)

- **NEW [`docs/runbooks/agent-best-practices.md`](runbooks/agent-best-practices.md)** — recommend single `.sln` index over per-`.csproj` for .NET monorepos (saved 3× wall time on CortexFlow). Document the dedup behavior so users understand why duplicates inflate counts but not actual data. Link from `MCP-GUIDE.md` `ActivateAgent` section.
- **`docs/ARCHITECTURE.md`** — the `embeddable kinds` list moves out of `IndexingPipeline.cs` comments into prose with a one-paragraph rationale.

These are required for #1 + #2 anyway, so plan them inline.

---

## Phasing

| Wave | Items | Goal |
|------|-------|------|
| 1 | #1 (Health threshold) + cross-cutting docs | Stop the false PARTIAL alarms. Land the doc skeleton (HEALTH-METRICS, ARCHITECTURE update, agent-best-practices). |
| 2 | #2 (rename + API.md) + alias compat | Honest API. Future-proof for the deprecation in v0.8.0. |
| 3 | #3 (investigation only) | ADR with measurements. Fix lands in v0.7.1 or v0.8.0 if warranted. |

**Tag plan**:

- `v0.7.0` cut after waves 1 + 2 land. Wave 3 ADR can be in 0.7.0 even if the fix isn't.
- If the AGE edge fix proves valuable + small, fold it into 0.7.0; otherwise hold for 0.7.1.

---

## Out of scope for v0.7.0

- Marketing screenshots (depends on user-supplied imagery).
- Chunk resume / `upload_id` (Tier 3 backlog item, no real-world pain yet).
- `last_error` field on DEGRADED Health (Tier 3, defer until users ask).
- Agent auto-update on watch start (Tier 3, AgentUpdater exists but not wired — defer).
