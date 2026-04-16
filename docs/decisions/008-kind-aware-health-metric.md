# ADR-008: Kind-aware health metric for `list_repositories`

**Status:** Accepted
**Date:** 2026-04-15

## Context

The `list_repositories` MCP tool gained a `Health:` line in v0.6.0 (commit `5468e3b`) to surface repositories where the vector path silently dropped data — the original symptom of [issue #1](https://github.com/DT-Tuan/cortexplexus/issues/1). The first implementation compared `with_embedding / total_symbols` against a 90% threshold:

```text
_ when coverage < 0.9 => "PARTIAL — {h.WithEmbedding}/{h.Symbols} ({coverage:P0}) symbols embedded..."
_ => "OK — {h.Symbols} symbols, {h.WithEmbedding} with embeddings ({coverage:P0})"
```

Three weeks later, on 2026-04-15, a real-world re-index of the CortexFlow .NET solution surfaced the design flaw. Output was:

```text
Health: PARTIAL — 2130/5273 (40 %) symbols embedded. Some semantic hits will be missing.
```

The repo was healthy. The `40%` was the natural rate for a typical .NET project: only a subset of symbol kinds are embeddable (`class`, `method`, `interface`, `struct`, `record`, `function`, `type`, `document`, `section`), and the rest (`field`, `property`, `event`, `constructor`, `enum`, `parameter`, `namespace`, etc.) are intentionally skipped — embedding them adds noise to semantic search without unlocking new questions. CortexFlow is a Blazor-heavy stack with many properties, so 40% is exactly what we expect.

Other production repos hit during the same session showed the same pattern:

| Repo | Embedding ratio | Old label | Reality |
|---|---|---|---|
| CortexFlow | 40% | PARTIAL | Healthy |
| CortexPlexus | 80% | PARTIAL | Healthy |
| TaskSchedulerApp | 73% | PARTIAL | Healthy |

Three for three: the metric was wrong in the way that matters most — false-alarming a healthy repo into looking broken.

## Decision

Compute the Health metric against **embeddable symbols** only, not against the full `code_symbols` row count. The new query:

```sql
SELECT repo_id,
       COUNT(*) AS total,
       COUNT(*) FILTER (WHERE kind IN ('class','method','interface','struct',
                                       'record','function','type','document','section')) AS embeddable,
       COUNT(*) FILTER (WHERE embedding IS NOT NULL) AS with_embedding
FROM code_symbols
GROUP BY repo_id;
```

The thresholds against the new denominator are tighter than the old absolute ratio because the failure mode they exist to catch (embedding-pipeline silent drop) doesn't half-fail — it either succeeds or it nukes a whole batch:

```
embeddable == 0 AND total > 0           → OK (no embeddable kinds, e.g. config-only)
with_embedding / embeddable >= 0.9      → OK
with_embedding / embeddable >= 0.5      → PARTIAL
with_embedding == 0 AND embeddable > 0  → DEGRADED
```

The user-facing string also changes: include both numbers and label them clearly:

```
OK — 5273 symbols, 2130 embeddings (100% of 2130 embeddable kinds)
```

so a future reader of `list_repositories` understands exactly what `100%` is the percent of. The full spec lives in [`docs/HEALTH-METRICS.md`](../HEALTH-METRICS.md).

## Alternatives considered

### Alternative A: Lower the threshold to 25%

Set `coverage < 0.25 → PARTIAL`. Cheap to implement (one number change, no SQL).

**Rejected** because:
- It silently accepts a repo where 25% of vectors are missing as "OK", which is exactly the issue-#1-style failure mode we're trying to catch.
- "25%" has no semantic meaning and would have to be re-tuned every time the embeddable-kinds list changes.

### Alternative B: Drop the Health line, surface only DEGRADED

Show no line on healthy repos; show `Health: DEGRADED` only when `with_embedding == 0 AND embeddable > 0`.

**Rejected** because:
- The PARTIAL state (some chunks succeeded, some failed) is a real and informative signal — users who hit Ollama rate limits during a long run want to know which repos got a partial pass.
- Users want to *see* the embedding count regardless, even on healthy repos, as a sanity check.

### Alternative C: Compute the metric in the agent, not the server

The agent already knows which symbols it sent and what the response said about persistence (after the v0.6.0 honest-response work). It could compute the ratio locally and report it.

**Rejected** because:
- The metric is per repo over time, not per upload. The agent doesn't know about prior runs unless we add state.
- `list_repositories` is a server-side query about server-side state; pushing the calculation to the agent inverts the responsibility.

### Alternative D: Track expected vs actual separately during indexing

Server records "we attempted N embeddings, succeeded with M" in a `repo_indexing_runs` table per chunk. Health reads the most recent run's success rate.

**Considered for v0.7.0+**: this would also catch the `Item #2` rename and let us show "last run had 12 partial failures" in DEGRADED. Not blocking ADR 008 — the kind-aware ratio query is enough for now and ships in days, not weeks.

## Consequences

- **Pro:** every healthy .NET repo on every existing deployment immediately stops false-alarming as PARTIAL. The first user-visible win of v0.7.0.
- **Pro:** the user-facing string is self-explanatory ("100% of 2130 embeddable kinds") — no lookup required.
- **Pro:** the embeddable-kinds list now has a single canonical home in code (`IndexingPipeline.cs`), the SQL filter (`GraphTraversalTools.cs`), and the doc (`HEALTH-METRICS.md`). All three must move together; CONTRIBUTING.md will add a check item.
- **Con:** the SQL query is slightly more expensive (two `COUNT(*) FILTER` instead of one). On the LXC reference deployment with 3 repos and ~5K rows each, the query runs in <30 ms — acceptable.
- **Con:** the embeddable-kinds list is now duplicated between `IndexingPipeline.cs` and `GraphTraversalTools.cs`. Mitigation: extract to a `static readonly string[] EmbeddableKinds` in `CortexPlexus.Core` so both reference one constant. Tracked as a follow-up in v0.7.0.

## Verification

- New unit test in `CortexPlexus.Mcp.Tests` mocking the data source so the query returns mixed-kind rows; assert the rendered label is `OK`.
- Manual: re-run `list_repositories` against the LXC after the change ships; expect CortexFlow / CortexPlexus / TaskSchedulerApp to all show `OK`.

## References

- Issue #1: pgvector type cache poisoned on fresh DB boot — the original failure mode that motivated the Health line.
- v0.6.0 commit `5468e3b` — original Health implementation with the wrong denominator.
- [`docs/HEALTH-METRICS.md`](../HEALTH-METRICS.md) — user-facing spec.
- [`docs/PLAN-v0.7.0.md`](../PLAN-v0.7.0.md) Item #1 — workplan this ADR settles.
