# ADR-011: Three-tier scope model — session / project / global

**Status:** Accepted
**Date:** 2026-04-17

## Context

Agent memories need to be isolated along some axis — otherwise "User A's preference about CortexFlow" leaks into "User B's query about TaskSchedulerApp". Research on existing systems ([agent-memory-system.md §2](../research/agent-memory-system.md)) shows three common patterns:

- **Flat** (single namespace) — simplest, but loses isolation. Used by some toy implementations.
- **Per-actor** (Mem0-style) — good for multi-user SaaS, over-engineered for single-user self-hosted.
- **Tiered** (session / project / user — Letta-style) — matches developer mental model.

CortexPlexus runs self-hosted, typically single-user, but across multiple projects. The scope model needs to answer: "Should this memory surface when the agent is working on *this* project, in *this* conversation?"

## Decision

**Three-tier scope: `session`, `project`, `global`.**

| Scope | `scope_id` semantics | Lifetime | Typical count |
|---|---|---|---|
| `session` | Client-supplied session UUID | Transient — decays fast (topic `note` / `todo`) | 0–20 per session |
| `project` | `repositories.id` | Persistent — decays slowly (topic `preference` / `pattern`) | 10–100s per project |
| `global` | `NULL` | Persistent — user-wide | < 10 total |

**`scope_id` is required** unless `scope='global'` (enforced by CHECK constraint at the DB layer). A project memory *must* name the repo; a global memory *must not* name one.

`recall_memory` can filter to a single scope or query all three (default `all`), ranked by relevance × decay.

## Why three tiers

1. **`session` matches the AI's natural unit of work.** A chat turn can set transient state ("debugging X") that shouldn't outlast the conversation. Without a session scope, these leak into future sessions as stale noise.

2. **`project` matches the repository boundary.** Most useful memories ("In CortexFlow, auth flows through X") are scoped to a specific codebase. This is also the natural join point for `related_fqns` — symbols belong to one repo.

3. **`global` is escape hatch for user-wide truths.** "User prefers tests written with xUnit" applies across projects. It's deliberately awkward to use (< 1% expected) so it doesn't become a dumping ground.

No fourth tier (e.g., per-user in a multi-user deployment) for MVP — the self-hosted model doesn't need it, and adding it later is additive (new CHECK constraint value, new filter).

## Rejected alternatives

### Flat (single namespace)

- Pros: simplest implementation, no scope-id logic.
- Cons: every memory bleeds into every recall. No way to clear session-transient state.
- **Why rejected**: forces the user to either self-prefix every memory ("[session-X] ...") or live with noise. That's Quarry's current pain point.

### Two-tier (project / global)

- Pros: smaller surface than three-tier.
- Cons: no transient scope → session-specific state either becomes permanent pollution or has to be manually forgotten at session end.
- **Why rejected**: the decay reaper handles long-term pruning, but session state should die *immediately* when the conversation ends. A dedicated scope with a short half-life gives that property declaratively.

### Five-tier (session / task / project / user / global)

- Pros: fine-grained.
- Cons: more boundaries than users can reason about; agents will pick wrong tier.
- **Why rejected**: over-engineered for MVP. If a real pain point emerges, add sub-scopes within `project` via `topic` (already an enum) rather than multiplying the `scope` axis.

### Per-actor (Mem0-style)

- Pros: clean isolation in multi-user / multi-agent deployments.
- Cons: CortexPlexus is currently single-user self-hosted; adds no value; requires actor identity plumbing through the MCP transport (no standard way).
- **Why rejected**: premature for single-user scenarios. If CortexPlexus ever grows multi-tenant, re-introduce as a new axis (scope_id becomes `(actor_id, project_id)` tuple for `project` scope).

## Consequences

### Positive

- Deterministic filtering: "what memories apply to *this* conversation in *this* project?" maps directly to `scope IN ('session', 'project', 'global') AND scope_id IN (session_id, project_id, NULL)`.
- Reaper can apply different thresholds per topic within each scope without more logic.

### Negative / risks

- **Scope drift** — AI agents might save everything as `session` (safest to the agent) or `global` (easiest). Mitigation: tool docstrings explicitly recommend `project` as default; Wave 2 review after real usage.
- **Renaming repos** breaks project memories if `repositories.id` changes. `id` is stable across re-index (it's the UUID from the repositories table, not derived from name), so in practice this is safe. Documented in MEMORY-SYSTEM.md.

## Follow-ups

- Observe scope distribution after 2 weeks of usage. If >50% of memories are `session`, we're catching too much transient state; tighten the `session` half-life or guide agents to `project` more forcefully.
- Consider `scope='task'` in a future release if a pain point emerges (e.g., a work-in-progress branch that wants its own scope).
