# Research — CortexPlexus as Memory System for AI Coding Agents

**Status**: research / proposal. NOT committed to a roadmap version yet. Large scope; revisit with fresh eyes before writing a PLAN-v0.X.Y.md.

**Last updated**: 2026-04-16

**Context of this document**: We noticed during the v0.7.0 verification run that AI coding agents (Claude Code, Cursor, Antigravity, Aider, …) rely heavily on scattered `.md` files to store session memory, project conventions, ADRs, TODOs, and "last context" snapshots. This is noisy, ungoverned, not searchable, and duplicated per-agent. CortexPlexus already has the infra (pgvector HNSW, AGE graph, tsvector BM25, RRF fusion, MCP transport) to be a first-class memory backend. This document surveys the landscape, distills six useful patterns and five anti-patterns, and sketches a MVP we can validate in v0.8.0.

---

## 1. Problem statement

The "memory" footprint of a coding agent today:

- **Per-session `.md` dumps** — `.claude/memory/*`, `.cursor/context.md`, progress notes, TODO files. Stale fast, no index.
- **Per-project `CLAUDE.md` / `AGENTS.md`** — conventions, architectural notes. Hand-maintained, out-of-date.
- **Markdown vaults** (Serena `.serena/memories/`, Quarry captures) — first step up: structured files with opinionated conventions, but no semantic retrieval, no TTL, no lifecycle.
- **General-purpose memory SaaS** (Mem0, Zep, Letta) — solid memory infra but do NOT understand code structure. Mem0 store "user prefers dark mode"; none of them can link a memory to `CortexFlow.Core.Services.AgentOrchestrator.ProcessAsync` the way we can.

The gap: **semantic, graph-linked, self-hosted memory that understands code at the FQN level.** No competitor occupies this exact slot. CortexPlexus is a natural extension because the graph + vector + FTS + MCP stack is already there.

Risks if we build it wrong: memory becomes a markdown-in-database replay of the same noise problem — just centralised. The rest of this document is about how to NOT do that.

---

## 2. Competitive landscape

### 2.1 General-purpose agent memory

| | **Mem0** | **Zep (Graphiti)** | **Letta (MemGPT)** |
|---|---|---|---|
| Architecture | Vector + optional graph + KV | Temporal knowledge graph | Tiered OS-style (core / archival / recall) |
| Differentiator | Actor-aware tagging, metadata filter, `A.U.D.N.` LLM consolidation | **Bitemporal model** — every fact has `valid_from/valid_until` + `ingested_from/ingested_until` | Agent **self-manages** memory via tool calls |
| LongMemEval (GPT-4o) | 49.0% | **63.8%** | (different paradigm) |
| OSS | Yes + cloud SaaS | Open-core ($25/mo Flex for full) | Yes |
| Code-aware? | ❌ | ❌ | ❌ (agent runtime, not coding-specialised) |
| Positioning | "Universal memory layer" | "Recall what matters" | "Stateful agent runtime" |

None of the three understands call graphs, DI containers, or EF Core mappings. They store facts about conversations.

### 2.2 Coding-focused (actual competitive set)

| Tool | Code intelligence depth | Has memory? | Notable trick |
|------|---|:---:|---|
| **Serena** (oraios) | LSP-based symbol retrieval | ✅ `.serena/memories/*.md` | Markdown files, meaningful names, agent reads selectively |
| **Quarry** (punt-labs) | Hybrid search 20+ formats | ✅ | **Claude Code hooks**: SessionStart / PostToolUse / PreCompact auto-capture |
| **Claude Context** (zilliztech) | Code search MCP | ❌ | Pure search, well-optimised |
| **Probe** (probelabs) | ripgrep + tree-sitter | ❌ | AST-aware but memoryless |
| **agent-memory** (DNG-ai) | ❌ | ✅ long-term | "Documents patterns the codebase doesn't make obvious" |
| **CortexPlexus** | **Roslyn-deep C#** + 7 TS-languages | ❌ (yet) | DI / EF Core / middleware / API route analysis — unique |

**Gap analysis for CortexPlexus**:

Advantages no competitor has:
1. Roslyn-depth C# (DI container resolution, EF Core entity-to-table map, Minimal API with `[controller]` expansion, middleware pipeline order, NuGet audit).
2. Self-hosted + 2 Docker containers + MIT + $0.
3. Unified graph + vector + FTS with RRF fusion.
4. MCP-native (first-class), 26 tools already shipped.

Things competitors have that we don't (yet):
- Agent memory as a first-class concept.
- Temporal memory with validity windows (Zep).
- Auto-capture via Claude Code hooks (Quarry).
- Per-actor / per-project scoping (Mem0).
- LLM-driven memory consolidation (Mem0 A.U.D.N.).

---

## 3. Six patterns worth adopting

### 3.1 LLM-as-judge consolidation (Mem0 A.U.D.N.)

On every `memory_save`, vector-search top-k similar existing memories, hand them to an LLM alongside the candidate, let the LLM decide **ADD / UPDATE / DELETE / NOOP**.

Why this matters: prevents "I like cheese" + "Actually I don't like cheese" being stored as two orthogonal facts. The LLM understands supersession and contradiction in ways no `if/else` can.

Cost: one LLM call per save. Decide later whether to always-on or opt-in per save.

Adoption: defer to v0.9.0+ — not MVP-critical, but high-ROI once a memory store exists.

### 3.2 Bitemporal non-lossy fact tracking (Zep / Graphiti)

Every fact has two separate timelines:
- `valid_from / valid_until` — when the fact was true in the world.
- `ingested_from / ingested_until` — when the agent learned the fact.

When a new fact contradicts an old one, set `valid_until = now()` on the old fact, insert the new fact — **do not delete**. Supports "what did we believe on 2026-03-15?" queries, compliance audits, rollback.

Why powerful: retroactive corrections are handled natively. `FactX was true from 2026-01-01 but we only learned it on 2026-04-10 when user corrected us`.

Adoption: Big scope (breaks the obvious "edit a row in place" mental model). Good v1.0 feature, probably not v0.8.

### 3.3 Tiered memory with strategic forgetting (Letta / MemGPT)

Three tiers:
- **Core** (always in context, like RAM): active persona, goals, current state.
- **Archival** (retrieved on demand, like disk): facts, past conversations.
- **Recall** (message history, searchable): raw transcripts.

Strategic forgetting = summarization + targeted deletion via agent tool calls. When context window fills, agent emits a `summarize_and_flush` action.

Why useful even if we're not an agent runtime: the **summarization primitive** — older memories of same topic get compressed into one fact — is a powerful noise-reduction technique for any memory store.

Adoption: summarization-on-overflow could be a v0.9.0 background job; tiered storage is out of scope (we're a memory service, not an agent runtime).

### 3.4 Multi-dimensional scoping (Mem0)

Mem0 scopes memories along five axes: `user_id / agent_id / session_id / run_id / app_id`. Hierarchy: **workspace → project → user**. Each agent gets a dual-tier: **private** memory (isolated) + **shared** memory (cross-agent with read/write policies).

"Vault" concept: an isolated memory unit with its own directory, vector index, and history. Can be per-agent, per-project, or shared as a coordination layer.

Why critical for us: a dev machine running watch agents on 3 repos simultaneously must not leak memory between them. A TODO about `CortexFlow.AgentOrchestrator` surfacing as a result when searching the `CortexPlexus` repo is a UX disaster.

Adoption: MANDATORY from v0.8.0. Our version: `global / project:<name> / session:<uuid> / symbol:<fqn>` scope hierarchy.

### 3.5 User-editable markdown sync (Serena)

Serena writes `.serena/memories/*.md` with meaningful filenames (Code Style, Conventions, Architecture). User can read, edit, commit them with the repo. Agent reads selectively at session start.

Downsides: no semantic search, manual file management, memories bloat.

Upside: **user-editable source of truth** is powerful — devs can curate what the agent "should remember".

Adoption: offer `memory_import(dir=.serena/memories)` as a migration tool; also consider a `memory_sync` mode where specific project-scope memories write back to `.cortexplexus/memories/*.md` for user review. Nice-to-have, v0.9.0+.

### 3.6 Hook-based auto-capture (Quarry)

Registers Claude Code lifecycle hooks: **SessionStart** auto-indexes working dir, **PostToolUse** auto-ingests fetched URLs, **PreCompact** snapshots context before compaction fires.

Why it's a UX win: user doesn't have to remember to save memory. The agent writes opportunistically when the editor emits signals.

Adoption: best added AFTER the core memory API is stable (v1.0.0). Hooks are thin glue.

---

## 4. Five anti-patterns to actively avoid

These map directly to the "memory becomes a markdown-in-database replay" risk.

### 4.1 Pure cosine-similarity retrieval

Top-1 = "most semantically similar", which is **not** the same as "most useful right now". Classic example: search "auth" returns a TODO from 3 months ago that was already closed. Fix: combine `similarity × recency × importance`.

### 4.2 Context poisoning

Stale or wrong facts enter the context window, agent reasons on top, errors compound silently. Mitigation: assign lower score to stale memories, prune when freshness score drops below threshold. "Prune and continue" beats "prune and alert".

### 4.3 100% retention as a bug

From Fazm's "Memory Triage for AI Agents": assuming everything is worth storing long-term is wrong. Not every note deserves permanent space. Mitigation: TTL on by default; users opt-in to "persistent" via explicit scope choice.

### 4.4 Context distraction

Too many memories loaded into context → model "defaults to repeating historical behavior" instead of reasoning fresh. Studied phenomenon in recent memory-consistency papers. Mitigation: **scoring, not just storage** — top-k=5 strong hits beats top-k=50 lukewarm hits.

### 4.5 Exponential decay is too simple

Pure `exp(-λ·t)` is uniform. Real workflows have "fast early forgetting" for ephemeral notes and "delayed forgetting" for persistent decisions. **Weibull decay** has a shape parameter that expresses both. Start simple (exponential with per-scope half-life) and upgrade if needed.

---

## 5. Proposed design for CortexPlexus

### 5.1 Scope hierarchy

Four tiers:

```
global                    # user preferences, span all projects (no TTL)
  └── project:<name>      # project-specific facts (default no TTL)
       └── session:<uuid> # current task state (TTL 24h)
       └── symbol:<fqn>   # attached to a specific code symbol (TTL by topic)
```

Rationale: most coding-agent memory is project-scoped (conventions, decisions, TODOs against methods). Session scope prevents mid-task context from bleeding across days. Symbol scope (phase 2 after FQN-linking lands) enables "TODO on this method" semantics.

### 5.2 Topic taxonomy — bounded vocabulary

Six topics enforced by `CHECK` constraint (no free-form to prevent drift):

| Topic | Meaning | Default TTL | Typical scope |
|-------|---------|-------------|---------------|
| `architecture` | Design rationale not obvious from code | ∞ | project |
| `convention` | Style/pattern agreement | ∞ | project, global |
| `decision` | Short-form ADR-equivalent | ∞ | project, symbol |
| `gotcha` | Non-obvious pitfall | ∞ | project, symbol |
| `todo` | Pending work | **14 days** (auto-expire) | project, symbol |
| `context` | Current task working state | **24 hours** | session only |

Why bounded: free-form tags sprout infinite synonyms (`note`, `misc`, `temp`, `session_notes`, `chatlog`, …). Six topics force a categorisation discipline. If a memory doesn't fit one of these, it probably shouldn't be in the memory store.

### 5.3 TTL policy

Auto-populate `expires_at` at save time based on `(scope, topic)` unless user overrides:

```csharp
expires_at = (scope, topic) switch
{
    ("session:*", _)            => now + 24h,
    (_, "todo")                 => now + 14d,
    (_, "context")              => now + 24h,
    _                           => null  // no expiry for architecture/convention/decision/gotcha
};
```

Users can bump by calling `memory_save(..., extend_ttl: 30d)` which updates `expires_at`. Explicit intent-to-keep beats implicit infinite growth.

### 5.4 Retrieval scoring formula

```
final_score = 0.5 × semantic_similarity
            + 0.3 × importance_weight
            + 0.2 × freshness_factor
```

Where:
- `semantic_similarity` ∈ [0, 1] — cosine against query embedding
- `importance_weight` ∈ [0, 1] — normalised from user-set importance (1-5, default 3)
- `freshness_factor` = Weibull decay against age, with per-scope half-life:
  - `session`: 8 hours
  - `symbol:todo`: 7 days
  - `project:architecture / decision / convention`: **no decay** (flat 1.0)

Pre-retrieval filter: always `WHERE expires_at IS NULL OR expires_at > NOW()`.

Post-retrieval filter: if top-1 score < 0.4, return "no relevant memory" rather than forcing a weak hit. Anti-hallucination.

### 5.5 Forgetting hygiene

Daily cron on the server:

```sql
-- Hard expire
DELETE FROM agent_memories WHERE expires_at IS NOT NULL AND expires_at < NOW();

-- Session leftover (even if caller forgot to set expires_at)
DELETE FROM agent_memories
WHERE scope LIKE 'session:%' AND created_at < NOW() - INTERVAL '24 hours';

-- Archive low-importance + decayed
UPDATE agent_memories SET archived = true
WHERE importance < 2 AND last_accessed < NOW() - INTERVAL '90 days';
```

Plus **per-(scope, topic) bounded size**: max 50 memories per bucket. On overflow: LLM A.U.D.N. consolidation (v0.9+) OR simple LRU eviction (v0.8 MVP).

### 5.6 Schema sketch

```sql
CREATE TABLE agent_memories (
    id              BIGSERIAL PRIMARY KEY,
    scope           TEXT NOT NULL,        -- 'global' | 'project:<n>' | 'session:<u>' | 'symbol:<fqn>'
    topic           TEXT NOT NULL CHECK (topic IN ('architecture','convention','decision','gotcha','todo','context')),
    content         TEXT NOT NULL,
    embedding       vector(768),
    importance      SMALLINT DEFAULT 3 CHECK (importance BETWEEN 1 AND 5),
    created_at      TIMESTAMPTZ DEFAULT NOW(),
    updated_at      TIMESTAMPTZ DEFAULT NOW(),
    expires_at      TIMESTAMPTZ,
    last_accessed   TIMESTAMPTZ DEFAULT NOW(),
    access_count    INT DEFAULT 0,
    archived        BOOLEAN DEFAULT FALSE,
    linked_fqns     TEXT[],               -- Phase 2: soft link to code_symbols.fqn
    metadata        JSONB                 -- escape hatch for future fields
);

CREATE INDEX idx_memories_scope ON agent_memories (scope) WHERE NOT archived;
CREATE INDEX idx_memories_topic ON agent_memories (topic) WHERE NOT archived;
CREATE INDEX idx_memories_expires ON agent_memories (expires_at) WHERE expires_at IS NOT NULL;
CREATE INDEX idx_memories_embedding ON agent_memories USING hnsw (embedding vector_cosine_ops) WHERE NOT archived;
```

Deliberately **separate table from `code_symbols`** — different lifecycle, different ownership.

### 5.7 MCP tool surface

Four tools for v0.8.0 MVP:

- `memory_save(content, scope, topic, importance?, expires_at?, linked_fqns?)`
- `memory_recall(query, scope?, topic?, top_k=5, include_archived=false)`
- `memory_list(scope, topic?, limit=20)` — browse mode
- `memory_delete(id | scope_prefix, dry_run=false)`

---

## 6. Phased rollout

### Phase 1 — v0.8.0 MVP (validate demand first)

In scope:
- 4 MCP tools (save/recall/list/delete)
- Schema with bounded topic + scope
- TTL auto-population + daily cron GC
- Combined scoring (similarity + importance + freshness)
- HEALTH-METRICS.md-style user doc for memory labels & hygiene

Out of scope (defer based on measured usage):
- LLM A.U.D.N. consolidation (Mem0)
- Bitemporal model (Zep)
- Summarisation-on-overflow (Letta)
- Hook-based auto-capture (Quarry)
- Symbol FQN linking
- Vault / multi-user ACL
- Markdown file sync

Success criterion for "proceed to Phase 2": ≥3 distinct users (ourselves, 2 test users) actively saving and recalling memory over 2-4 weeks, with Health metric (to be defined) showing `ratio_recalled_then_acted_upon >= 0.5`.

### Phase 2 — v0.9.0 (graph linking + consolidation)

If Phase 1 measured positive signal:
- `linked_fqns` cross-reference from `agent_memories` to `code_symbols`
- `get_callers(X)` / `explore_topic(X)` surface linked memories alongside code
- LLM A.U.D.N. on save (opt-in flag)
- Summarisation worker: when `(scope, topic)` hits bound, LLM condenses oldest N into 1

### Phase 3 — v1.0.0 (temporal + auto-capture + polish)

- Bitemporal model (`valid_from / valid_until`) for fact supersession
- Claude Code hook integration (SessionStart / PostToolUse / PreCompact)
- CLI: `cortexplexus memory-admin dump / prune / import-from-serena`
- Public positioning: "Code + memory intelligence for AI coding agents"

---

## 7. Open design questions

These need decisions before turning this doc into `docs/PLAN-v0.8.0.md`:

1. **Topic taxonomy** — is 6 values (architecture/convention/decision/gotcha/todo/context) the right cut? Too few? Too many?
2. **Default TTL** — is 14d for `todo` right for real workflow cadence, or should it be 7d / 30d?
3. **Importance weight** — keep (1-5, user-set) or remove and rely on recency × similarity only? Mem0 has implicit signal via access-count; we might copy that instead.
4. **File sync** (Serena-style) — offer a `.cortexplexus/memories/*.md` export for user curation, or DB-only?
5. **Scope nomenclature** — `project:CortexFlow` vs `project.CortexFlow` vs `/CortexFlow/`? Any of these works; prefer `:` for readability in shell-free contexts.
6. **Cron vs lazy GC** — run daily GC job, or lazy-on-read (skip expired on recall without deleting)? Cron simpler, lazy skips a running process.

---

## 8. Decision gate before Phase 1 kickoff

**Build only if** the following are true:

- We want CortexPlexus positioning to be "code **and** memory intelligence for AI agents" — not just "code search".
- We accept a second product surface (memory) increases maintenance burden proportionally.
- We have bandwidth to measure Phase 1 usage and actually kill the feature if demand is low.

**Do not build if**:

- The 1-3 users we have can get by with `.md` files and that pain isn't material.
- We prefer keeping CortexPlexus focused on code-only retrieval (which is a valid strategic choice — don't dilute).

---

## 9. References

General-purpose agent memory:
- [Mem0: Building Production-Ready AI Agents (arXiv 2504.19413)](https://arxiv.org/abs/2504.19413)
- [Mem0 architecture breakdown — A.U.D.N. cycle](https://memo.d.foundation/breakdown/mem0)
- [Zep: Temporal Knowledge Graph Architecture for Agent Memory (arXiv 2501.13956)](https://arxiv.org/abs/2501.13956)
- [Graphiti: Knowledge Graph Memory for an Agentic World (Neo4j blog)](https://neo4j.com/blog/developer/graphiti-knowledge-graph-memory/)
- [MemGPT: Towards LLMs as Operating Systems (arXiv)](https://arxiv.org/pdf/2310.08560)
- [Agent Memory: How to Build Agents that Learn and Remember (Letta blog)](https://www.letta.com/blog/agent-memory)
- [State of AI Agent Memory 2026 (Mem0 blog)](https://mem0.ai/blog/state-of-ai-agent-memory-2026)
- [Graph-Based Memory Solutions Top 5 Compared (Mem0 blog)](https://mem0.ai/blog/graph-memory-solutions-ai-agents)
- [5 AI Agent Memory Systems Compared — 2026 Benchmark Data (dev.to)](https://dev.to/varun_pratapbhardwaj_b13/5-ai-agent-memory-systems-compared-mem0-zep-letta-supermemory-superlocalmemory-2026-benchmark-59p3)
- [Best AI Agent Memory Frameworks 2026 (Atlan)](https://atlan.com/know/best-ai-agent-memory-frameworks-2026/)

Noise / hygiene / scoring:
- [Memory Triage for AI Agents — Why 100% Retention Is a Bug (Fazm)](https://fazm.ai/blog/ai-agent-memory-triage-retention-decay)
- [Solving Freshness in RAG: Simple Recency Prior (arXiv 2509.19376)](https://arxiv.org/pdf/2509.19376)
- [7 Steps to Mastering Memory in Agentic AI Systems (MachineLearningMastery)](https://machinelearningmastery.com/7-steps-to-mastering-memory-in-agentic-ai-systems/)
- [Governing Evolving Memory in LLM Agents (SSGM Framework, arXiv)](https://arxiv.org/html/2603.11768)
- [Collaborative Memory: Multi-User with Dynamic Access Control (arXiv 2505.18279)](https://arxiv.org/html/2505.18279v1)

Coding-focused tooling:
- [Serena — MCP toolkit for coding (GitHub)](https://github.com/oraios/serena)
- [Quarry — local semantic search + agent memory (GitHub)](https://github.com/punt-labs/quarry)
- [Claude Context — code search MCP (GitHub)](https://github.com/zilliztech/claude-context)
- [Probe — AI-friendly semantic code search (GitHub)](https://github.com/probelabs/probe)
- [agent-memory — long-term memory for Claude Code & OpenCode (GitHub)](https://github.com/DNG-ai/agent-memory)
