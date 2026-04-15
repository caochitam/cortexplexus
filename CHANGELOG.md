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

### Planned

- **v0.7.0 work breakdown** is tracked in [`docs/PLAN-v0.7.0.md`](docs/PLAN-v0.7.0.md). Three items surfaced from the v0.6.0 verification run on CortexFlow (5,273 symbols / 19,437 edges / 9m17s): kind-aware Health threshold (P1), rename of the misleading `EmbeddingsPersisted` response field (P2, breaking), and an investigation into AGE edge upsert per-chunk scaling (P3). Each item ships with a doc/ADR before code.

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
