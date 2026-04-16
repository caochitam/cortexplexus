# Health metrics

Spec for the `Health:` line emitted by the `list_repositories` MCP tool and the `/api/repositories` REST endpoint.

## Why a Health line at all

Issue #1 (closed in v0.6.0) showed that `/api/index/results` could return HTTP 200 with valid-looking stats while the vector store had silently dropped every row. From the user's side, the only signal was eventual "search returns nothing." The Health line is the early-warning signal: a query for the repo's row counts that runs after each `list_repositories` call and reports anomalies in plain English.

## Embeddable vs non-embeddable kinds

CortexPlexus parses many symbol kinds, but only a subset is embedded into the vector store. This is a deliberate cost / signal trade-off: embedding a property accessor or a constructor adds noise to semantic search without unlocking new questions.

**Embedded kinds** (vector row gets a non-null `embedding`):

```
class · method · interface · struct · record · function · type · document · section
```

**Non-embedded kinds** (vector row exists but `embedding IS NULL`):

```
field · property · event · constructor · enum · enum_member ·
parameter · namespace · di_registration · api_endpoint · middleware ·
config_key · dbcontext · relationship-only entries
```

The implementation reference lives in [`src/CortexPlexus.App/Indexing/IndexingPipeline.cs`](../src/CortexPlexus.App/Indexing/IndexingPipeline.cs) (search `embeddable`). If you change that filter, update this doc in the same PR.

## Health label rules

Computed per repo from a single Postgres query:

```sql
SELECT
  COUNT(*) AS total,
  COUNT(*) FILTER (WHERE kind IN ('class','method','interface','struct',
                                  'record','function','type','document','section')) AS embeddable,
  COUNT(*) FILTER (WHERE embedding IS NOT NULL) AS with_embedding
FROM code_symbols
WHERE repo_id = @repoId;
```

| Label | Condition | What it means | What to do |
|---|---|---|---|
| **OK — no embeddable symbols** | `embeddable == 0` AND `total > 0` | Repo only has config / namespaces / docs (no classes or methods). Vector search will return nothing for this repo, but that's expected. | Nothing. Search by graph or full-text instead. |
| **OK — N symbols, M with embeddings (P%)** | `with_embedding / embeddable >= 0.9` | Healthy. Almost every embeddable symbol made it to the vector store. | Nothing. |
| **PARTIAL — N/M (P%) embeddable symbols embedded** | `0.5 <= with_embedding / embeddable < 0.9` | Some embedding API calls failed (rate limits, network, model timeout). Search will miss roughly `1 − P` of embeddable hits. | Re-run indexing, or call `force_reindex(name)` and retry. |
| **DEGRADED** | `with_embedding == 0` AND `embeddable > 0` | The vector path failed completely for this repo (typically issue-#1-style: pgvector type-cache poison, embedding provider down). Semantic search returns nothing for this repo. | Restart the app container; if it persists, check server logs for `Failed to upsert vector` warnings. |
| **EMPTY** | `total == 0` | Repo registered but no symbols in the database. The indexing run wrote nothing. | Re-run `ActivateAgent` / `index_from_local`. |
| **UNKNOWN** | DB query unavailable | The MCP handler couldn't reach the database probe (tests / mis-configured deploy). | Not user-actionable; reflects deploy/test scaffolding only. |

The exact denominator is **embeddable symbols**, not total symbols. A repo of 5,000 symbols where 2,000 are embeddable and all 2,000 got an embedding is **OK**, not "40% PARTIAL". Older deployments (≤ v0.6.0) compared to the total and reported every healthy .NET repo as PARTIAL — see [ADR 008](decisions/008-kind-aware-health-metric.md) for the rationale.

## Example output

```text
Indexed repositories:

  Name: CortexFlow
  Path: _agent/CortexFlow
  Last indexed: 2026-04-15 16:09:54
  Health: OK — 5273 symbols, 2130 embeddings (100% of 2130 embeddable kinds)

  Name: SmallConfigOnly
  Path: _agent/SmallConfigOnly
  Last indexed: 2026-04-15 14:00:00
  Health: OK — no embeddable symbols (config-only repo)

  Name: AfterIncidentRepo
  Path: _agent/AfterIncidentRepo
  Last indexed: 2026-04-15 13:00:00
  Health: DEGRADED — 1500 symbols indexed, 0 with embeddings. Semantic search will fail for this repo; check server logs for vector-upsert warnings.
```

## When to call `list_repositories`

- **Right after activating an agent**, to confirm indexing landed cleanly.
- **Before any semantic search round-trip** if you suspect stale data.
- **In CI sanity checks** that fail on `PARTIAL` / `DEGRADED` / `EMPTY` repos before running expensive query suites.

## Related

- [`ADR 008 — kind-aware health metric`](decisions/008-kind-aware-health-metric.md) — why embeddable kinds, not total.
- [`ARCHITECTURE.md` § Embedding kinds](ARCHITECTURE.md) — the full enumeration.
- [`MCP-GUIDE.md` § list_repositories](MCP-GUIDE.md) — tool reference.
