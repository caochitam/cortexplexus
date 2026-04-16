# Changelog

All notable changes to CortexPlexus are documented here.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

Versioning notes:

- The repo version covers the **server + stack** (Docker images, MCP tools, docs).
- The **Local Agent** has its own version string (`CortexPlexus.Core.AgentInfo.Version`); bumped independently when wire-protocol or CLI changes.
- Users on `ghcr.io/dt-tuan/cortexplexus:main` always get the latest commit to `main`. Users on a pinned tag (e.g. `:0.6.0` for exact, `:0.6` for latest patch in the 0.6.x series) get a stable release. Docker image tags follow OCI convention (bare semver, no `v` prefix); the git tag is `v0.6.0`.

## [Unreleased]

### Changed

- **Docs**: corrected the `Requirements` section of [`README.md`](README.md) — actual idle memory is ~150 MB (app 99 MiB + postgres 25 MiB), not the 4 GB previously claimed. New guidance is tiered: 1 GB min / 2 GB recommended / 4 GB only for 20K+ symbol repos or co-located Ollama.

### Fixed

- **`list_repositories` Health metric is now kind-aware** ([ADR 008](docs/decisions/008-kind-aware-health-metric.md), [HEALTH-METRICS.md](docs/HEALTH-METRICS.md)). Previously every healthy .NET repo showed `PARTIAL` because the metric compared `embeddings / total_symbols`, but field/property/event/constructor are intentionally not embedded — so a typical .NET repo's ratio caps around 30-50%. CortexFlow showed `PARTIAL — 2130/5273 (40%)` despite being 100% healthy. Now compares `embeddings / embeddable_symbols` (where embeddable kinds = class, method, interface, struct, record, function, type, document, section). Output also shows both numerators ("100% of 2130 embeddable kinds") so the calculus is self-explanatory.

### Added

- **NEW [`docs/HEALTH-METRICS.md`](docs/HEALTH-METRICS.md)** — user-facing spec for the `Health:` line: every label, condition, and recovery action.
- **NEW [`docs/decisions/008-kind-aware-health-metric.md`](docs/decisions/008-kind-aware-health-metric.md)** — ADR explaining why embeddable kinds (not total) is the right denominator, with three rejected alternatives.
- **NEW [`docs/runbooks/agent-best-practices.md`](docs/runbooks/agent-best-practices.md)** — single-`.sln` indexing (3× faster than per-`.csproj`), watch-mode behavior, throughput tuning, force-reindex flow.
- **NEW `CortexPlexus.Core.EmbeddableKinds`** — single source of truth for the embeddable-kind allow-list, used by `IndexingPipeline`, `AgentApiEndpoints`, and `GraphTraversalTools.ListRepositories`. Replaces three duplicated `s.Kind is "class" or "method" or ...` literals.
- **`docs/ARCHITECTURE.md` §3.5** now enumerates embeddable vs non-embeddable kinds with rationale, replacing the implicit knowledge in `IndexingPipeline.cs` comments.
- **README + MCP-GUIDE** link to `HEALTH-METRICS.md` and `agent-best-practices.md` from the docs index and the "First 3 commands" section.

### Changed

- **`/api/index/results` response schema**: `embeddingsPersisted` and `embeddingsFailed` are now deprecated aliases (kept as computed properties for one release). Prefer the new `symbolsPersisted` / `symbolsFailed` — the historical names were misleading because the value counts symbol rows, not embedding rows. New `vectorRowsWritten` field counts symbols whose `embedding` column ended up non-null, distinguishing "row inserted with NULL embedding" (expected for non-embeddable kinds) from "row inserted with vector". Old (1.1.0) agents keep working against new servers; new (1.2.0+) agents keep working against old servers via the wire-compat picker in `LocalIndexer.UploadAck`. Removal target: v0.8.0. See [`docs/API.md`](docs/API.md).
- **`VectorUpsertResult` record** in `CortexPlexus.Core` gains a `VectorRowsWritten` member alongside `Persisted` / `Failed`. Used by the App-side `/api/index/results` handler to populate the new response field.

### Added

- **NEW [`docs/API.md`](docs/API.md)** — concise REST API reference for `/api/agent/version`, `/api/agent/download`, `/api/index/results`, and `/api/index/{name}/hashes`. Documents the new field names, the deprecated aliases, and the wire-compat policy.

### Fixed

- **AGE edge upsert scaling** ([ADR 009](docs/decisions/009-age-edge-upsert-scaling.md)). Edge phase on CortexFlow (19K edges / 4 chunks) slowed linearly: 11.8s → 20.9s → 28.5s → 30.4s (total 91.6s). Root cause: AGE's `MERGE (a)-[r:Type]->(b)` performs a sequential scan on the edge label table per edge, and the table grows with each chunk. Fix: for bulk indexing (≥500 edges), delete all outgoing edges for affected source vertices first, then `UNWIND + CREATE` (not MERGE) with `MATCH` on existing vertices. Projected improvement: ~15× (91s → ~6s). Incremental watch-mode still uses MERGE for small batches.

## [0.6.0] — 2026-04-15

Focus: indexing **correctness** (silent-failure bugs closed), indexing **UX** (progress + health), agent **reliability** (watch-mode resilience, auto-start), and deployment **hygiene** (pinning, image-bundled agent tarballs, runbooks).

### Added

- **MCP progress notifications** during server-side indexing — `index_from_local`, `index_from_git`, and the full pipeline emit phase events (`detect → parse → embed → graph → vector`) via `notifications/progress`. The embedding step reports per-batch sub-progress (`2 + done/total`) so the progress bar moves continuously during the slowest phase. Clients that don't send a `progressToken` silently ignore the reports.
- **`list_repositories` health probe** — each repo line now shows `Health: OK / PARTIAL / DEGRADED / EMPTY / UNKNOWN`, computed from a live join against `code_symbols`. Detects the "registered but empty" state that silently happened when `/api/index/results` returned 200 but the vector upsert rolled back.
- **`force_reindex(name)` MCP tool** — wipes the server-side file-hash cache for a given repo so the next `ActivateAgent` / `index_from_local` treats every file as changed. Symbols are not deleted in place; FQN upsert overwrites them cleanly.
- **Honest failure surfacing end-to-end** — `IVectorStore.UpsertAsync` now returns `VectorUpsertResult { Persisted, Failed }`. `/api/index/results` forwards these counts plus a `Warnings[]` string list. The Local Agent treats any non-zero `EmbeddingsFailed` as a hard error and aborts the upload. Replaces the old path where a 100% vector-upsert failure still returned HTTP 200 with input-side stats.
- **`IProgress<ProgressNotificationValue>` parameter** on `IndexingPipeline.IndexAsync` (non-MCP callers pass null).
- **`VectorUpsertResult` + `AgentInfo`** types in `CortexPlexus.Core` (SSOT for the agent version string shared between the agent CLI and the server endpoint).
- **Agent `watch` mode honors `.cortexplexusignore`** — `ProjectFileWatcher` loads user patterns at construction and filters events against them in addition to the built-in exclude list.
- **`FileSystemWatcher` overflow recovery** — on `OnError` (typically OS kernel buffer overflow during mass renames / git checkout), the watcher enumerates the watched tree and re-queues every eligible file instead of silently dropping the burst.
- **`ComputeFileHash` retry** — 3 attempts with 150/300ms backoff on `IOException` / `UnauthorizedAccessException` so the IDE's mid-save file-lock race no longer drops files from the batch.
- **Image-bundled agent tarballs** — `src/CortexPlexus.App/Dockerfile` now publishes the agent for `linux-x64` / `win-x64` / `osx-x64` and ships the tarballs in `/app/_agent/`. `/api/agent/download` resolves image-bundled first, then `${Workspace__Path}/_agent/` as an operator override. `docker compose pull` now ships the matching agent — no more manual SCP on version bumps.
- **`ActivateAgent` auto-detects server URL** from the incoming MCP request's `Host` header (multi-host friendly). Output now has a 7-step decision tree with explicit PASS/FAIL markers, a connectivity check (Step 2), a version check (Step 3), and clear branching for the AI assistant to follow.
- **`force_reindex` + `list_repositories` + 26-tool help** reflect the new tool count; the agent's `get_help("tools")` is updated.
- **Runbook: [`docs/runbooks/maintenance.md`](docs/runbooks/maintenance.md)** — disk & Docker housekeeping with a weekly cron template and two emergency-recovery variants (one that protects `cortexplexus_*` volumes).
- **Runbook: [`docs/runbooks/agent-auto-start.md`](docs/runbooks/agent-auto-start.md)** — systemd user unit (instance-parameterised), Windows Task Scheduler PowerShell, NSSM service for headless Windows, macOS LaunchAgent.
- **Marketing docs**: [`docs/INTRODUCTION.md`](docs/INTRODUCTION.md) (EN), [`docs/INTRODUCTION-VI.md`](docs/INTRODUCTION-VI.md) (VN), [`docs/PITCH-DECK.md`](docs/PITCH-DECK.md) — one-pagers + 8-slide outline grounded in real benchmarks (R18 HNSW 556× speedup, 26 MCP tools, 693 tests).
- **Antigravity MCP setup** in `README.md` + `docs/MCP-GUIDE.md` — uses `serverUrl` (not `url`); client table and schema differences documented so AI assistants don't copy the Claude Code pattern by accident.
- **MCP/IDE config gitignore hygiene**: `.mcp.json`, `.cursor/`, `.vscode/mcp.json`, etc. are git-ignored; `.mcp.json.example` is tracked as the template.

### Changed

- **Agent version bumped to `1.1.0`** (from `1.0.0`) — backward-compatible wire protocol; old servers accept new agents, new servers accept old agents (persist-counts default to zero).
- **Local Agent parses the `/api/index/results` response body** to check `EmbeddingsFailed`; on any persist error it throws with a clear message instead of silently succeeding.
- **Postgres base pinned** to `apache/age:release_PG17_1.6.0`; **pgvector pinned** to `v0.8.0` with `-march=x86-64-v2` so image builds run on CI with AVX-512 don't produce binaries that SIGILL on older host CPUs (Kaby Lake and similar).
- **`AgentInfo.Version` single source of truth**: moved to `CortexPlexus.Core`; server `/api/agent/version` reads it directly instead of duplicating the string.
- **`ActivateAgent` MCP tool signature** gained `IProgress<ProgressNotificationValue>` and `CancellationToken`; output completely rewritten as a 7-step recipe with verification hooks.
- **README `Why CortexPlexus?` section** links to the full marketing/pitch docs.
- **MCP-GUIDE "Developing on CortexPlexus itself"** clarifies that project-level `.mcp.json` shadows `--scope user`; documents the right escape hatches.

### Fixed

- **Issue [#1](https://github.com/DT-Tuan/cortexplexus/issues/1)** — pgvector type cache poisoned on fresh DB boot. Npgsql probed `pg_type` before `CREATE EXTENSION vector` ran, cached "vector type unknown", and every subsequent vector read/write failed while HTTP `/api/index/results` still returned 200. Fixed by running `CREATE EXTENSION IF NOT EXISTS vector; CREATE EXTENSION IF NOT EXISTS age` on a raw `NpgsqlConnection` **before** the `NpgsqlDataSource` singleton is built, with a 30×1s retry loop for startup races.
- **Postgres SIGILL on DELETE CASCADE / schema init** — CI rebuild produced pgvector binaries using AVX-512 paths that older Xeon/Core hosts (Intel Core i3-7100 / Kaby Lake) could not execute. Fixed by pinning `pgvector` to `v0.8.0` and compiling with `OPTFLAGS="-O2 -march=x86-64-v2"`.
- **`apache/age:latest` silent major bump** — upstream rolled `:latest` from PG17 to PG18 mid-session, breaking fresh deploys. Pinned to a release tag.
- **Test regression** — `GraphTraversalToolsTests.ListRepositories_*` were failing in CI because the new DI parameter `NpgsqlDataSource` was added as non-nullable; changed to `NpgsqlDataSource? = null` and guarded the health probe.
- **`ActivateAgent` recipe used to hand back `http://localhost:8080`** to AI clients connecting from a different host — recipe steps all failed with connection refused, the AI improvised wrong URLs. Now auto-detected from the MCP connection.
- **Stale `LocalIndexer` response parsing** — agent never looked at `EmbeddingsFailed`; partial uploads silently reported "done". Fixed alongside the server-side honest-response change.
- **`.gitignore` inline comments were interpreted as patterns** — rules for `.claude/`, `.git-backup/`, `deploy.sh` were never actually matched; they appeared as untracked in every `git status` despite the rule. Comments moved to their own lines; file re-audited.
- **Docker Compose image tags** use `:main` (CI publishes branch name, not `:latest`) for the default deployment.

### Security

- **`secrets sanitize` pass confirmed in place** in the embedding pipeline — symbols go through `ISecretsScanner.Sanitize` before text leaves the dev host for Ollama / Gemini.
- **Issue-report skill** (`~/.claude/skills/report-cortexplexus-bug/`) has hardened PII scrubbing: 11 regex rules (Gemini / OpenAI / GitHub PAT / JWT / Slack / email / Linux+macOS+Windows user paths / RFC1918 private IPs / public IPs). Rules moved out of a markdown table (where `\|` was silently interpreted as escaped pipe and broke alternation).

### Infrastructure

- **`/etc/cron.weekly/docker-prune`** installed on the reference LXC host (pruned 6.6 GB of stale layers + 118 MB dangling during install).
- **CI `docker-publish.yml`** now also builds and tags postgres image (`ghcr.io/dt-tuan/cortexplexus-postgres:main`) per commit; app image now bundles agent tarballs.
- **`.mcp.json.example`** at repo root for fresh clones; `.mcp.json` is git-ignored for per-developer URL/auth overrides.

### Breaking

None. All wire-protocol and MCP-tool changes are additive; old clients see new fields as ignored JSON, new clients on old servers see defaults.

## [0.5.0] — 2026-04-12

Initial public release.

- Open-source Code Intelligence Platform: Roslyn (C# deep) + Tree-sitter (TS / JS / Python / Java / Go / Rust / PHP) → PostgreSQL 17 + AGE + pgvector HNSW + tsvector BM25 → MCP.
- 26 MCP tools across search, navigation, .NET deep analysis, quality / observability, composite.
- Two-container Docker Compose deployment; Ollama (offline) or Google Gemini (free tier) for embeddings.
- 693 tests passing (~85% coverage).
- GitHub Release: agent tarballs for linux-x64 / win-x64 / osx-x64 + SHA256SUMS.

[Unreleased]: https://github.com/DT-Tuan/cortexplexus/compare/v0.6.0...HEAD
[0.6.0]: https://github.com/DT-Tuan/cortexplexus/compare/v0.5.0...v0.6.0
[0.5.0]: https://github.com/DT-Tuan/cortexplexus/releases/tag/v0.5.0
