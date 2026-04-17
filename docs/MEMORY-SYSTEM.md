# Agent Memory System

Introduced in v0.8.0. Opt-in. Default disabled.

## What memory is for

A **first-class, semantic, graph-linked memory store** for AI coding agents (Claude Code, Cursor, Antigravity, Aider, …). It replaces scattered per-agent files like `.claude/memory/*.md`, `.cursor/context.md`, and hand-maintained `CLAUDE.md` blocks with one centralised, searchable, auto-decaying store.

**Use memory for:**

- User preferences the agent should respect across sessions ("prefer async/await over callbacks")
- Project-specific patterns that are non-obvious from code (conventions, gotchas, workarounds)
- Architecture decisions that aren't in an ADR yet
- TODOs and follow-ups that should survive the current conversation
- Bug reproduction notes tied to specific symbols (linked via `related_fqns`)

**Don't use memory for:**

- Things already in code (use `search_code` / `get_impact_analysis` / existing MCP tools)
- Things already in `CLAUDE.md` or ADRs (those are authoritative and versioned)
- Ephemeral task state inside a single conversation (use the conversation itself)
- PII, credentials, or API keys (the system rejects these via `ISecretsScanner`)

## Scopes

Every memory has exactly one scope:

| Scope | Lifetime | `scope_id` | Example |
|---|---|---|---|
| `session` | Single AI conversation | session UUID from the client | "User is debugging X — remembered for this chat" |
| `project` | Per-repository | `repositories.id` | "In CortexFlow, auth middleware logs to `/var/log/auth.jsonl`" |
| `global` | Cross-project | `NULL` | "User prefers terse explanations" |

**Guidance**: default to `project`. Use `session` for transient state that shouldn't survive. Use `global` **sparingly** (< 1% of memories) — global memories affect every project's recall and are the most prone to drift.

When in doubt, `recall_memory` returns all scopes mixed, ranked by relevance × decay score.

## Topics (bounded enum)

Every memory has an optional topic:

| Topic | Decay half-life* | Meaning |
|---|---|---|
| `preference` | 365 days | Sticky: user likes X, avoids Y |
| `pattern` | 180 days | Code/design pattern specific to this project |
| `decision` | 180 days | Why X was chosen over Y |
| `bug` | 90 days | Known issue or workaround (fades once presumably fixed) |
| `todo` | 30 days | Short-lived follow-up |
| `note` | 60 days | Default for unclassified memories |

\* Half-life is approximate — actual decay uses a Weibull curve (shape k=1.5). See §Decay below.

## Decay

Every memory has an `importance` value in [0, 1] (default 0.5). Its effective score at recall time:

```
score = importance × exp( -(t / λ)^k )
    where:
      t = days since last_accessed_at
      k = 1.5 (Weibull shape — forgets faster than exponential)
      λ = topic half-life (days, from the table above)
```

### Worked examples

| Memory | importance | t | topic | decay | score | kept? |
|---|---:|---:|---|---:|---:|:---:|
| "User prefers terse answers" | 0.8 | 400 days | preference | 0.33 | 0.26 | ✅ |
| "Bug in UserService.Delete" | 0.5 | 45 days | bug | 0.38 | 0.19 | ✅ |
| "TODO: review PR #42" | 0.5 | 45 days | todo | 0.11 | 0.055 | ❌ auto-forgotten |
| "Misc note, low importance" | 0.2 | 90 days | note | 0.07 | 0.014 | ❌ auto-forgotten |

**Auto-forget threshold**: score < 0.1.

**Reaper**: a background service (`MemoryReaper`) scans the table every 24h (configurable) and deletes memories below the threshold. `recall_memory` also applies a live filter so expired-but-not-yet-reaped memories don't surface.

### Why Weibull

- **Exponential** (k=1) forgets too slowly at the tail — old memories hang around polluting search.
- **Linear** has no principled threshold and doesn't match human-memory intuition.
- **Weibull with k=1.5** accelerates the decay at the tail, matching the Ebbinghaus-style forgetting curve and giving clean auto-forget behaviour.

See [ADR-012](decisions/012-memory-decay-weibull.md) for the full justification and rejected alternatives.

## Symbol linking

Memories can link to code symbols via `related_fqns: string[]`. This is a **soft link** — no foreign key to `code_symbols` — because FQNs change across rename refactors. If a symbol is renamed, the old FQN is retained in the memory's `related_fqns` but won't match a current symbol. This is a deliberate trade-off: we keep the historical context even if the exact symbol has moved.

Example:

```json
{
  "content": "Team prefers async/await over callbacks in controller actions",
  "scope": "project",
  "scope_id": "518c2817-68c1-46c6-ac67-1971cb1db713",
  "topic": "preference",
  "related_fqns": ["CortexFlow.API.Controllers.UserController"]
}
```

When an agent later calls `get_impact_analysis` on `UserController` with `include_memories=true`, the memory surfaces alongside the impact report.

## Enabling memory

Default: **disabled**. Nothing is stored, nothing is queried, tools return a clear "memory is disabled" message.

### Enable via `appsettings.json`

```json
{
  "Memory": {
    "Enabled": true,
    "ReapIntervalHours": 24,
    "MaxMemoriesPerScope": 10000,
    "DefaultImportance": 0.5
  }
}
```

### Or via environment variable (Docker / CI)

```
Memory__Enabled=true
Memory__ReapIntervalHours=24
Memory__MaxMemoriesPerScope=10000
```

Restart the server. `list_repositories` reports `Memory: enabled (N items)` so you can see the feature is live.

### Disabling later

Set `Memory__Enabled=false` and restart. Existing data is **retained** in the `agent_memories` table — re-enabling restores it untouched. To wipe, drop the table manually (`TRUNCATE agent_memories;`) or use the maintenance runbook.

## Tool reference

Four MCP tools, all gated by `Memory.Enabled`:

### `save_memory`

Saves a new memory after PII/secrets scan.

```
Parameters:
  content: string (required, 1..4000 chars)
  scope: 'session' | 'project' | 'global' (required)
  scope_id: string (required if scope != 'global')
  topic: 'preference'|'bug'|'pattern'|'decision'|'todo'|'note' (optional)
  importance: number 0..1 (optional, default from MemoryOptions)
  related_fqns: string[] (optional — soft link to code symbols)
Returns: { id: uuid, stored: true }
Errors:
  - 'memory_disabled' if Memory.Enabled=false
  - 'validation' if content is empty, too long, or contains secrets
  - 'scope_id_invalid' if scope='project' and scope_id is not a known repo
```

### `recall_memory`

Semantic + filtered search, decay-weighted.

```
Parameters:
  query: string (required, 1..500 chars)
  scope: 'session' | 'project' | 'global' | 'all' (optional, default 'all')
  scope_id: string (optional)
  topic: string (optional)
  related_fqn: string (optional)
  limit: int (optional, default 10, max 50)
Returns: { memories: [{ id, content, scope, topic, importance, score, related_fqns, created_at }] }
Side effect: bumps access_count + last_accessed_at for returned rows (refreshes their decay).
```

### `list_memories`

Pure filter + paginate, no embedding compute. For management / audit.

```
Parameters:
  scope: 'session' | 'project' | 'global' | 'all' (optional, default 'all')
  scope_id: string (optional)
  topic: string (optional)
  limit: int (optional, default 50, max 500)
Returns: { count, memories: [...] }
```

### `forget_memory`

Explicit delete. Use when the agent stored something wrong.

```
Parameters:
  id: uuid (required)
Returns: { forgotten: true } or { forgotten: false, reason: 'not_found' }
```

## Storage model

Single table `agent_memories` in the same PostgreSQL database as `code_symbols`. See [ADR-010](decisions/010-memory-storage-reuse-postgres.md) for the "why reuse Postgres" rationale.

Columns:

- `id` — UUID primary key
- `content` — text (1–4000 chars)
- `scope` — `'session'|'project'|'global'` (CHECK constraint)
- `scope_id` — text, required unless `scope='global'`
- `topic` — text (optional)
- `importance` — real, 0..1
- `related_fqns` — text[], GIN-indexed
- `embedding` — `vector(768)`, HNSW-indexed (same model as `code_symbols`)
- `created_at` / `last_accessed_at` — timestamptz
- `access_count` — int

Indexes: HNSW on embedding, GIN on related_fqns, B-tree on (scope, scope_id), partial on topic.

## Known limitations (v0.8.0 MVP)

- **No LLM consolidation** — storing "I like cheese" then "I don't like cheese" keeps both. A future release (v0.9.0) will add Mem0-style A.U.D.N. logic.
- **Soft FQN links can rot** — symbol renames break the link silently. `related_fqns` is retained verbatim.
- **Per-session state not auto-cleared** — session memories stick around until the reaper gets them. Clients can proactively call `forget_memory` per-ID.
- **No bitemporal validity** (Zep-style `valid_from/valid_until`) — everything is "now" or "forgotten".

## See also

- [ADR-010](decisions/010-memory-storage-reuse-postgres.md) — why reuse Postgres
- [ADR-011](decisions/011-memory-scope-model.md) — 3-tier scope decision
- [ADR-012](decisions/012-memory-decay-weibull.md) — Weibull decay (added in wave 2)
- [ADR-013](decisions/013-memory-opt-in-default.md) — default disabled
- [`docs/research/agent-memory-system.md`](research/agent-memory-system.md) — landscape research (Mem0, Zep, Letta, Serena, Quarry)
- [`docs/PLAN-v0.8.0.md`](PLAN-v0.8.0.md) — doc-first work breakdown
