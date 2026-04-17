# ADR-012: Memory decay uses a Weibull curve with per-topic half-life

**Status:** Accepted
**Date:** 2026-04-17

## Context

Agent memories accumulate over time. Without decay, the store becomes either:

- **Polluted** — old TODOs, stale session notes, and one-off observations resurface in recall and bias the AI toward outdated context.
- **Unbounded in size** — disk fills with content the user no longer needs.
- **Over-demanding on users** — requiring explicit `forget_memory` calls shifts storage hygiene onto the user, who has better things to do.

Needed: a decay function that

1. Keeps high-value memories (preferences, architectural decisions) for a long time.
2. Lets short-lived memories (TODOs, session notes) expire quickly.
3. Respects the user-supplied `importance` input.
4. Produces a score comparable across all memories for ranking and auto-forget.
5. Is computable in SQL for the hot recall path (no round-trip per row).

## Decision

Use a **Weibull curve** with **shape parameter `k = 1.5`** and a **per-topic scale parameter `λ`** (days).

```
score(memory, now) = importance × exp( -((now - last_accessed_at) / λ)^k )
```

Per-topic `λ` values (days):

| topic         | λ   | Rationale |
|---------------|----:|-----------|
| `preference`  | 365 | User preferences are sticky; 1-year default feels right. |
| `pattern`     | 180 | Codebase patterns change but not constantly. |
| `decision`    | 180 | Matches `pattern` — decisions are the "why" behind patterns. |
| `bug`         |  90 | Bugs fade once presumably fixed. |
| `todo`        |  30 | Short-lived follow-ups. |
| `note`        |  60 | Default for unclassified. |
| (null topic)  |  60 | Treated as `note`. |

**Auto-forget threshold**: `score < 0.1`. The background `MemoryReaper` deletes matching rows on each scan (default every 24h). `recall_memory` also applies the same threshold as a live filter so expired-but-unreapeed rows don't surface.

**Score refresh on access**: a successful `recall_memory` bumps `last_accessed_at = now()` for returned rows. This means a memory that keeps being useful stays high-scored; a memory nobody cares about decays naturally.

## Why Weibull with k=1.5

The Weibull survival function `exp(-(t/λ)^k)` has three useful properties:

1. **Controllable tail shape via `k`**:
   - `k = 1` → pure exponential (memoryless). Forgets slowly at the tail.
   - `k < 1` → "infant mortality" — forgets fast early, then plateaus.
   - `k > 1` → "wear-out" — forgets slowly early, accelerates at the tail.
2. **`k = 1.5` matches the Ebbinghaus forgetting curve reasonably well** (rough empirical fit for human memory in cognitive-science literature).
3. **Clean auto-forget threshold** — because the tail accelerates, reaching `exp(-(t/λ)^1.5) < 0.1` happens in finite reasonable time. For `λ = 30` (todo), that's ~45 days; for `λ = 365` (preference), ~545 days.

### Shape verification

For a `todo` with `importance = 0.5`:

| t (days) | decay = exp(-(t/30)^1.5) | score |
|---------:|--------------------------:|------:|
| 0        | 1.000 | 0.500 |
| 7        | 0.897 | 0.449 |
| 14       | 0.731 | 0.366 |
| 30       | 0.368 | 0.184 |
| 45       | 0.156 | 0.078 (forgotten) |
| 60       | 0.059 | 0.029 |

A `todo` with default importance auto-forgets around day 45 — sensible. A `preference` (λ=365) with `importance=0.8` still scores 0.21 after a year.

### SQL implementation

Because PostgreSQL has `exp()`, arithmetic operators, and `EXTRACT(EPOCH FROM ...)`, the score is a single expression:

```sql
importance * exp(
    -POW(
        EXTRACT(EPOCH FROM (now() - last_accessed_at)) / (86400.0 * CASE topic
            WHEN 'preference' THEN 365
            WHEN 'pattern'    THEN 180
            WHEN 'decision'   THEN 180
            WHEN 'bug'        THEN  90
            WHEN 'todo'       THEN  30
            WHEN 'note'       THEN  60
            ELSE                       60
        END),
        1.5
    )
)
```

This evaluates in the `ORDER BY` and `WHERE score >= 0.1` clauses without a round-trip — the hot path stays one query.

## Rejected alternatives

### Linear decay `score = importance × max(0, 1 - t/λ)`

- Pros: trivial to compute.
- Cons: no principled auto-forget threshold; cliff-edge forgetting is unintuitive.
- **Why rejected**: doesn't match human-memory intuition and produces surprising behavior near `t = λ`.

### Pure exponential `score = importance × exp(-t/λ)`

- Pros: memoryless, well-understood.
- Cons: at `t = 4λ`, score is still `0.018 × importance` — non-zero for a very long time. Tail pollution.
- **Why rejected**: Weibull with `k > 1` accelerates the tail without sacrificing the memoryless-on-access property.

### Half-life model `score = importance × 0.5^(t / half_life)`

- Pros: intuitive ("every N days, memory value halves").
- Cons: algebraically identical to exponential with `λ = half_life / ln(2)`. Same tail problem.
- **Why rejected**: same as pure exponential.

### Per-memory TTL (fixed expiry date)

- Pros: explicit, debuggable.
- Cons: users have to guess a TTL at save time; no relation to whether the memory is actually being used.
- **Why rejected**: ignores access patterns. A TODO that's been referenced daily for 6 months is more valuable than a TODO saved yesterday and never read.

### Importance-boosted exponential (hybrid)

- Pros: `score = (importance * 2)^t/λ` or similar — strong boost for high-importance memories.
- Cons: no principled basis for the exponent manipulation; behavior gets hard to reason about.
- **Why rejected**: ad-hoc; Weibull already multiplies by importance linearly, which is the clean separation of concerns.

## Consequences

### Positive

- Recall + auto-forget use the same score expression: one source of truth, no drift.
- Per-topic λ values are easy to tune after observing real usage.
- `importance` is a simple linear multiplier — users can reason about it directly.
- The reaper is a straight SQL `DELETE WHERE score < 0.1` — trivial to test.

### Negative / risks

- **Clock skew / time-travel** — if `last_accessed_at` is in the future (e.g., clock drift), `exp` of a negative exponent yields > 1 and the score can exceed `importance`. The table CHECK constraint doesn't guard this. Mitigation: the Postgres server's `now()` is canonical; we only set `last_accessed_at = now()`, so this shouldn't happen in practice. If it ever does, the memory scores higher temporarily — not corrupt, just temporarily over-weighted.
- **Tuning via config is not exposed** — the λ values are hardcoded in the SQL expression. If a user's use case demands different decay speeds, they must fork. Acceptable for v0.8.0; consider making λ configurable in v0.9.0 if a concrete use case emerges.
- **Auto-forget is a SQL `DELETE` not a soft-delete** — no tombstones. If a user wants to resurrect a forgotten memory, they can't. Mitigation: documented in MEMORY-SYSTEM.md; future release may add `archived` flag if requested.

## Follow-ups

- After 2 weeks of real usage, inspect topic distribution and average memory age. Tune λ values if one topic is consistently too sticky or too forgetful.
- Consider exposing `k` as a config knob in v0.9.0 if operators want to tune decay shape per deployment.
