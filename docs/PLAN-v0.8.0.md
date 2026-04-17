# Plan — v0.8.0 (Agent Memory System)

> **Status**: draft. 6 design decisions locked 2026-04-17. This file is the working spec — when an item ships, its section moves into the matching CHANGELOG entry under `[Unreleased]` and links back to the commit/PR.

## Context

Research groundwork in [`docs/research/agent-memory-system.md`](research/agent-memory-system.md) (landscape: Mem0, Zep, Letta, Serena, Quarry) identified a clear gap: **semantic, graph-linked, self-hosted memory that understands code at the FQN level**. No competitor occupies this slot.

v0.8.0 is the MVP — the minimum that delivers value without becoming a "markdown-in-database" replay of the same noise problem. Advanced patterns (LLM-as-judge consolidation, bitemporal validity, auto-capture hooks) are deliberately deferred to v0.9.0+.

---

## Design decisions (locked 2026-04-17)

| # | Decision | Chosen | Alternative rejected |
|---|---|---|---|
| Q1 | Storage backend | **Reuse PostgreSQL** — new table `agent_memories` in existing DB | Redis/SQLite (extra service, no benefit at MVP scale) |
| Q2 | Scope model | **3-tier: session / project / global** | Flat (loses project isolation); 5-tier (over-designed) |
| Q3 | Decay | **Weibull decay × importance score + auto-forget at score < 0.1** | Fixed TTL (fragile); manual-only (user burden) |
| Q4 | MCP tools | **4 tools: save / recall / list / forget** | 7+ (surface bloat); 2 (save+recall only — no governance) |
| Q5 | Symbol graph link | **Soft link — optional `related_fqns[]` field** | Hard FK (breaks when symbols renamed); no link (loses differentiator) |
| Q6 | Privacy/opt-in | **Default disabled — opt-in via config** | Default enabled (surprise data collection); per-call flag (friction) |

Decisions Q1–Q6 collectively define the MVP shape. If any decision changes mid-implementation, this plan must be revised *before* code moves forward.

---

## Doc-first principle (guardrails for this milestone)

For every item below:

1. **Doc first** — write the new behavior, schema, or threshold into the relevant doc (`MEMORY-SYSTEM.md`, ADR, `API.md`) **before** writing code. The doc becomes the spec.
2. **Test second** — capture the expected behavior in a failing test referencing the doc paragraph.
3. **Implement third** — code the change until the test passes.
4. **CHANGELOG fourth** — add a bullet under `[Unreleased]` per `CONTRIBUTING.md` rule. Cross-link to the doc.
5. **Verify on LXC fifth** — real AI agent (Claude Code in this repo) exercises the tools end-to-end.

If a code change ships without the doc, reviewer rejects.

---

## Item #1 — Data model + schema (P1, foundational)

**Decision**: PostgreSQL table `agent_memories` in the existing DB (Q1). No new service.

**Doc-first deliverables**

1. **NEW [`docs/MEMORY-SYSTEM.md`](MEMORY-SYSTEM.md)** — user-facing spec: what memory is for, what it isn't, the 3 scopes, the decay formula, when to save vs. not save. Target audience: AI agents (so they can decide correctly) and humans evaluating the feature.
2. **NEW [`docs/decisions/010-memory-storage-reuse-postgres.md`](decisions/010-memory-storage-reuse-postgres.md)** — ADR: why reuse Postgres vs separate store, how it interacts with existing `code_symbols` / AGE graph, backup story (pgdata volume already persistent).
3. **NEW [`docs/decisions/011-memory-scope-model.md`](decisions/011-memory-scope-model.md)** — ADR: why 3-tier scope (session/project/global), cardinality examples, when to promote session→project, when global is appropriate (≤ 1% of memories).

**Schema** (draft — finalize in MEMORY-SYSTEM.md first)

```sql
CREATE TABLE agent_memories (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    content TEXT NOT NULL,
    scope TEXT NOT NULL CHECK (scope IN ('session', 'project', 'global')),
    scope_id TEXT,                          -- repo_id for project, session_id for session, NULL for global
    topic TEXT,                             -- bounded enum: 'preference','bug','pattern','decision','todo','note'
    importance REAL NOT NULL DEFAULT 0.5,   -- 0..1
    related_fqns TEXT[],                    -- soft link to code_symbols.fqn (Q5)
    embedding vector(768),                  -- same model as code_symbols
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    last_accessed_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    access_count INT NOT NULL DEFAULT 0,
    CONSTRAINT scope_id_required CHECK (
        (scope = 'global') OR (scope_id IS NOT NULL)
    )
);

CREATE INDEX idx_memories_scope ON agent_memories (scope, scope_id);
CREATE INDEX idx_memories_topic ON agent_memories (topic) WHERE topic IS NOT NULL;
CREATE INDEX idx_memories_related_fqns ON agent_memories USING GIN (related_fqns);
CREATE INDEX idx_memories_embedding_hnsw ON agent_memories USING hnsw (embedding vector_cosine_ops);
```

**Code change**

- `src/CortexPlexus.Graph/Schema/Migrations.sql` — append the table + indexes (additive only, per existing migration policy).
- `src/CortexPlexus.Core/Models/AgentMemory.cs` — record type matching the schema.
- `src/CortexPlexus.Core/Abstractions/IAgentMemoryStore.cs` — interface: `SaveAsync`, `RecallAsync`, `ListAsync`, `ForgetAsync`, `RecordAccessAsync`.
- `src/CortexPlexus.Memory/` — new project, `AgentMemoryStore : IAgentMemoryStore` implementation.

**Test**

- `CortexPlexus.Memory.Tests` — new project. Integration tests against a real Postgres container (re-use `GraphTestFixture`).
- Assert: insert with scope='session' + scope_id='abc123' round-trips; insert with scope='global' + scope_id=null is allowed; insert with scope='project' + scope_id=null is rejected by CHECK constraint.

**Acceptance**

- Migration runs on a fresh v0.7.1 DB without error; on an upgraded DB, the table appears and existing data untouched (data-persistence guarantee from v0.7.0).
- `MEMORY-SYSTEM.md` is link-reachable from README and MCP-GUIDE.
- ADR 010 + 011 explain the choices with rejected alternatives.

**Scope estimate**: ~1 day including docs + ADRs + tests.

---

## Item #2 — Decay scoring + auto-forget (P1, governance)

**Decision**: Weibull decay × importance (Q3). Auto-forget when score < 0.1.

**Scoring formula** (finalize in MEMORY-SYSTEM.md §Decay)

```
Let t = days since last_accessed_at
Let k = shape (1.5 = memory forgets faster than exponential)
Let λ = scale-per-topic (days)
    preference   → 365   (preferences are sticky)
    pattern      → 180
    decision     → 180
    bug          → 90    (bugs fade once fixed)
    todo         → 30    (should be short-lived)
    note         → 60    (default)
    (null topic) → 60

decay(t) = exp( -(t/λ)^k )
score = importance × decay(t)

auto_forget: score < 0.1
```

Worked example: `topic=todo, importance=0.5, t=14 days → decay=exp(-(14/30)^1.5) = 0.73 → score=0.365 → kept`.
After `t=45 days → decay=exp(-(45/30)^1.5) = 0.16 → score=0.08 → auto-forgotten`.

**Doc-first deliverables**

1. **`docs/MEMORY-SYSTEM.md` §Decay** — formula with Desmos-plottable curves, worked examples per topic.
2. **NEW [`docs/decisions/012-memory-decay-weibull.md`](decisions/012-memory-decay-weibull.md)** — ADR: why Weibull, how λ was picked per topic, how importance defaults are set, rejected alternatives (linear, exponential-only, half-life).

**Code change**

- `src/CortexPlexus.Memory/MemoryScoring.cs` — pure function `Score(Memory, DateTimeOffset now) → double`.
- `src/CortexPlexus.Memory/MemoryReaper.cs` — background `IHostedService` running every 24h; deletes memories where score < 0.1. Configurable via `Memory:ReapIntervalHours` (default 24, min 1).
- `src/CortexPlexus.Memory/AgentMemoryStore.RecallAsync` — apply decay at query time: `ORDER BY importance * <decay-expression> DESC` and `WHERE importance * <decay-expression> >= 0.1`. Decay computed in SQL (no round-trip per row).

**Test**

- `CortexPlexus.Memory.Tests` — unit tests for `MemoryScoring`: known inputs → known outputs (regression lock).
- Reaper integration test: seed 100 memories with varied ages; run reaper; assert expected count remains and the reaper is idempotent.

**Acceptance**

- Memory store prunes itself without user intervention.
- A `note` saved 90 days ago with `importance=0.3` is auto-forgotten on the next reap.
- A `preference` saved 365 days ago with `importance=0.8` is still retained.

**Scope estimate**: ~1 day.

---

## Item #3 — MCP tools (P1, user-facing surface)

**Decision**: 4 tools — `save_memory`, `recall_memory`, `list_memories`, `forget_memory` (Q4).

**Doc-first deliverables**

1. **`docs/MCP-GUIDE.md` §Memory tools** — 4 tool descriptions, parameter specs, response examples, "when to use" guidance for AI agents.
2. **`docs/MEMORY-SYSTEM.md` §Tool reference** — same content, cross-linked.
3. **Tool docstrings** — follow the existing pattern in `DotNetTools.cs` / `GraphTraversalTools.cs` (rich markdown, examples).

**Tool specs**

### `save_memory`

```
Parameters:
  content: string (required, 1..4000 chars)
  scope: 'session' | 'project' | 'global' (required)
  scope_id: string (required if scope != 'global')
  topic: string (optional — one of preference|bug|pattern|decision|todo|note)
  importance: number 0..1 (optional, default 0.5)
  related_fqns: string[] (optional — soft link to symbols)
Returns: { id: uuid, stored: true }
Validation:
  - Reject if content contains obvious PII patterns (email, API key, credentials) — reuse ISecretsScanner.
  - Reject if scope='project' but scope_id is not a known repo_id.
```

### `recall_memory`

```
Parameters:
  query: string (required, 1..500 chars) — semantic search
  scope: 'session' | 'project' | 'global' | 'all' (optional, default 'all')
  scope_id: string (optional — filters when scope != 'all' and scope != 'global')
  topic: string (optional)
  related_fqn: string (optional — return only memories linked to this symbol)
  limit: int (optional, default 10, max 50)
Returns: { memories: [{ id, content, scope, topic, importance, score, related_fqns, created_at }] }
Ranking: importance × decay, then cosine similarity against query embedding. RRF fusion (reuse HybridQueryRouter pattern).
Side effect: RecordAccessAsync bumps access_count + last_accessed_at for returned rows.
```

### `list_memories`

```
Parameters:
  scope: 'session' | 'project' | 'global' | 'all' (optional, default 'all')
  scope_id: string (optional)
  topic: string (optional)
  limit: int (optional, default 50, max 500)
Returns: { count, memories: [...] }
Note: no embedding compute; pure filter + paginate for management UI.
```

### `forget_memory`

```
Parameters:
  id: uuid (required)
Returns: { forgotten: true } or { forgotten: false, reason: 'not_found' }
```

**Code change**

- `src/CortexPlexus.App/Mcp/Tools/MemoryTools.cs` — 4 `[McpServerTool]` methods.
- `src/CortexPlexus.App/Program.cs` — register `IAgentMemoryStore` in DI.
- Update the tool count in `get_help` text (currently says "26 tools" → becomes "30 tools").

**Test**

- `CortexPlexus.Mcp.Tests/MemoryToolsTests.cs` — 10+ tests covering all 4 tools: happy path, validation errors, PII rejection, scope-id validation, decay ordering.

**Acceptance**

- A fresh Claude Code session can `save_memory → recall_memory → list_memories → forget_memory` end-to-end against the LXC instance.
- PII in content (email/API key) is rejected with a clear error.
- Recall ordering matches the decay formula (bring-back test: seed fixture with known-score memories, assert rank order).

**Scope estimate**: ~2 days.

---

## Item #4 — Symbol graph integration (P2, differentiator)

**Decision**: Soft link via `related_fqns[]` (Q5). No FK constraint — FQNs can change with rename refactors.

**Doc-first deliverables**

1. **`docs/MEMORY-SYSTEM.md` §Symbol linking** — when to link a memory to an FQN, example ("Team prefers async/await over callbacks in `CortexFlow.API.Controllers.UserController`"), trade-offs of soft links (FQN renames break the link silently).
2. **`docs/ARCHITECTURE.md` §6 (new)** — memory as the 4th pillar alongside graph / vector / FTS. One-paragraph diagram update.

**Code change**

- `IAgentMemoryStore.RecallAsync` already accepts `related_fqn` parameter → implement the GIN-indexed filter.
- `GraphTraversalTools` — extend `get_impact_analysis` and `explore_topic` to optionally fetch relevant memories for the investigated symbols (behind a new `include_memories: bool` parameter, default false — opt-in to avoid surprise).

**Test**

- Integration test: save a memory with `related_fqns=['X.Y.Method']`, run `recall_memory(related_fqn='X.Y.Method')`, assert returned.
- Integration test: save memory linked to a class, `get_impact_analysis` on that class with `include_memories=true` returns the memory alongside impact data.

**Acceptance**

- Memories are retrievable by symbol FQN.
- Integration tools surface memories only when explicitly requested.
- When a symbol is renamed and re-indexed, existing memories keep the old FQN (known limitation, documented in MEMORY-SYSTEM.md).

**Scope estimate**: ~0.5 day.

---

## Item #5 — Privacy: opt-in config (P1, trust)

**Decision**: Default disabled. User opts in via config (Q6). Hard gate at the tool layer — calls fail clearly if disabled.

**Doc-first deliverables**

1. **`docs/MEMORY-SYSTEM.md` §Enabling memory** — exact config snippet, how to enable, how to disable, what happens to stored data when disabled (retained but inaccessible, or deleted on disable — decide in ADR).
2. **NEW [`docs/decisions/013-memory-opt-in-default.md`](decisions/013-memory-opt-in-default.md)** — ADR: why opt-in (trust + surprise-data-collection avoidance). Rejected alternatives: opt-out, per-call flag, per-repo flag.

**Config** (follow existing `appsettings.json` pattern)

```json
{
  "Memory": {
    "Enabled": false,
    "ReapIntervalHours": 24,
    "MaxMemoriesPerScope": 10000,
    "DefaultImportance": 0.5
  }
}
```

Environment variable overrides follow the standard `.NET` binding: `Memory__Enabled=true`.

**Code change**

- `src/CortexPlexus.App/Configuration/MemoryOptions.cs` — bound options record.
- All 4 MCP tools check `options.Value.Enabled` at the start → return `"Memory is disabled. Enable via Memory.Enabled in appsettings.json or Memory__Enabled=true env var. See docs/MEMORY-SYSTEM.md."` if false.
- `list_repositories` Health line mentions memory state: add a one-line suffix like `Memory: enabled (47 items)` or `Memory: disabled`.

**Test**

- Unit test: `save_memory` with `Memory:Enabled=false` returns the disabled-message error.
- Unit test: `save_memory` with `Memory:Enabled=true` succeeds.

**Acceptance**

- Fresh install has memory off. `save_memory` returns a clear "disabled, enable via config" message.
- After toggling on and restarting, tools work.
- The config path is documented in README + MEMORY-SYSTEM.md.

**Scope estimate**: ~0.5 day.

---

## Cross-cutting docs

- **`README.md` §Features** — add one line "Agent memory system (opt-in): semantic + scoped + auto-decay."
- **`docs/MCP-GUIDE.md` tool index** — list 4 new memory tools with brief descriptions.
- **`docs/decisions/000-index.md`** — register ADRs 010, 011, 012, 013.

---

## Out of scope for v0.8.0 (defer to v0.9.0+)

- **LLM-as-judge consolidation** (Mem0 A.U.D.N.) — requires design for which LLM, when to call, cost model. Defer.
- **Bitemporal validity** (Zep-style valid_from/valid_until) — useful but not MVP-critical. Revisit with real usage data.
- **Claude Code hook auto-capture** (Quarry-style) — requires client-side hook logic. Defer.
- **Memory export/import** — nice to have; users can use `pg_dump` on the table for now.
- **Web UI for memory management** — add to Graph Explorer later.
- **LLM-driven memory summarisation** (reduce 10 notes about X → 1 consolidated note) — defer to v0.9.0.

---

## Phasing

| Wave | Items | Goal |
|------|-------|------|
| 1 | #1 (schema) + #5 (opt-in config) | Foundation. Table + opt-in gate exists. Memory is off by default but deploy-ready. |
| 2 | #2 (decay) + #3 (MCP tools) | Usable MVP. User enables config → 4 tools work → memories decay correctly. |
| 3 | #4 (symbol link) + cross-cutting docs | Differentiator lands. README updated. v0.8.0 tag cut. |

**Tag plan**:

- `v0.8.0` after all 3 waves.
- If wave 2 takes longer than 2 days, split: tag `v0.8.0-beta` after wave 1+2, cut stable `v0.8.0` after wave 3.

---

## Open risks to revisit before starting wave 1

1. **Embedding cost for memories** — each `save_memory` triggers an embedding call. At scale (1000s of memories/day) this hits Ollama rate limits. Mitigation: reuse existing embedding infrastructure; batch is not needed at MVP traffic.
2. **Storage growth without bounded topics** — if `topic` is free-form, index bloats. Enforce bounded enum at the tool validation layer.
3. **Scope drift** — if AI agents save everything as `session`, the scope becomes useless. Mitigation: tool docstring guides "use session for transient state, project for things that should survive restart, global sparingly".
4. **Memory pollution from mistakes** — once a wrong memory lands, it influences recall. `forget_memory` is the escape hatch; `list_memories` helps find the culprit. Consider adding `admin` flag in v0.9.0 for bulk delete.

These are known. They don't block v0.8.0 — they shape how we evaluate it after release.
