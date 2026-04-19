# Runbook: Local Agent best practices

How to get the most out of the CortexPlexus Local Agent for typical workflows. Aimed at developers running CortexPlexus on a real codebase, not toy examples.

## Index a .NET solution as a single unit, not project-by-project

For .NET monorepos, **point the agent at the `.sln` file's directory once**, not at each `.csproj` separately:

```bash
# GOOD — single pass, Roslyn shares MSBuildWorkspace, deduplication is clean
dotnet cortexplexus-agent index "/path/to/MySolution" \
    --server http://192.168.50.14:8080 \
    --name MySolution
```

```bash
# AVOID — N separate uploads, each re-parses + re-uploads shared types
dotnet cortexplexus-agent index "/path/to/MySolution/01.Core" --name MySolution
dotnet cortexplexus-agent index "/path/to/MySolution/02.Infrastructure" --name MySolution
# ... (one call per .csproj)
```

### Why it matters

CortexFlow benchmark, 2026-04-15, on the LXC reference deployment:

| Strategy | Wall time | Symbols sent | Symbols persisted | Notes |
|---|---|---|---|---|
| Single-`.sln` pass (this run) | **9 m 17 s** | 5,904 | 5,273 unique | Deduplicates within Roslyn workspace before upload. |
| Per-`.csproj` (8 calls, prior run) | ~30 m | 11,633 (with cross-project dupes) | ~5,300 (after server dedup) | Each call re-parses shared types, server dedups via `ON CONFLICT (fqn)`, but the network + Ollama embedding cost is paid for every duplicate. |

3× faster, half the embedding-API cost, identical resulting graph.

### When per-`.csproj` is still useful

- One project broke during a multi-project run and you want to retry just it. (Use `force_reindex(name)` if you need to wipe state first.)
- The `.sln` is broken / out-of-sync with on-disk projects and only some `.csproj` parse cleanly.
- You're profiling a specific project's parse time in isolation.

## Watch mode for the day-to-day, foreground index for one-shot

After the initial `index`, switch to **watch mode** so the agent re-indexes only what changed:

```bash
nohup dotnet cortexplexus-agent watch "/path/to/MySolution" \
    --server http://192.168.50.14:8080 \
    --name MySolution > /tmp/cortexplexus-agent.log 2>&1 &
```

Watch mode:

- Loads `.cortexplexusignore` from the project root and applies it to incoming events (in addition to the hardcoded `bin/`, `obj/`, `node_modules/`, `.git/` excludes).
- Debounces 3 seconds — saves and refactors that touch many files in quick succession collapse into one re-index batch.
- Recovers from `FileSystemWatcher` buffer overflow by enumerating the tree (so a `git checkout` of a different branch does not silently miss changes).
- Retries `ReadAllBytes` up to 3 times when the IDE has the file locked mid-save.

For survival across reboots, see [`agent-auto-start.md`](agent-auto-start.md).

## Verify after every initial index

```text
ListRepositories()
→  Health: OK — 5273 symbols, 2130 embeddings (100% of 2130 embeddable kinds)
```

If you see `PARTIAL`, `DEGRADED`, or `EMPTY`, see [`docs/HEALTH-METRICS.md`](../HEALTH-METRICS.md) for the exact meaning and fix-it path. The `Health:` line is the first signal that something silently broke between agent and DB; trust it.

## Don't write source code into the upload pipeline

The agent uploads **metadata only** — FQNs, signatures, relationships, file SHA-256s. Source bodies never leave your machine. If you customize the agent (e.g. for a private fork), the same constraint applies: review `LocalIndexer.PostChunkAsync` to confirm the payload shape stays metadata-only.

The server's `ISecretsScanner` (the `BasicSecretsScanner` shipped with the platform) sanitizes any text that does flow through (signatures, doc comments) before it reaches the embedding provider. If you replace it with a stricter scanner for a private deployment, drop the implementation in DI before `services.AddSingleton<ISecretsScanner, ...>()` in `Program.cs`.

## Tune the embedding throughput

Ollama's `nomic-embed-text` (default) gives ~20 embeddings/second on a typical desktop CPU. For a 5,000-symbol repo that's ~4 minutes just on embedding. The v0.9.0 [benchmark harness](../../tests/CortexPlexus.Embedding.Benchmarks/) measured this and three alternative models on `ollama/ollama:0.20.0` with a 500-string synthetic corpus:

| Model | Dim | texts/s | vs default | Verdict |
|-------|----:|--------:|-----------:|---------|
| **`nomic-embed-text`** (default) | 768 | 21 | 1.0× | baseline |
| `mxbai-embed-large` | 1024 | 5.6 | 0.27× | **avoid** — 3.7× slower |
| `snowflake-arctic-embed:s` | 384 | 56 | **2.7×** | recommended for throughput, retrieval-tuned |
| `all-minilm` | 384 | **102** | **4.9×** | fastest, but general-purpose (not retrieval-tuned) |

Raw results: [`docs/benchmark-results/model-sweep-20260420.md`](../benchmark-results/model-sweep-20260420.md). Measured on a local PC (Windows + Docker); a GPU host will push these linearly higher.

### Which model to pick

- **Stay on `nomic-embed-text`** if you've already indexed — switching models means re-embedding every symbol (different dim → vectors incompatible). Only switch on a fresh install or if you're willing to force-reindex.
- **Switch to `snowflake-arctic-embed:s`** if you want ~2.7× throughput AND retrieval quality close to `nomic`. It's explicitly trained for search/RAG workloads.
- **Switch to `all-minilm`** only if you need maximum throughput and recall quality is secondary (it's a general-purpose model, not retrieval-specialized).
- **Avoid `mxbai-embed-large`** — larger parameter count makes it 3.7× slower on CPU-only Ollama, despite its MTEB ranking.

To switch, edit `.env` and force a re-index:

```bash
# .env on the server
EMBEDDING_PROVIDER=ollama
EMBEDDING_OLLAMA_MODEL=snowflake-arctic-embed:s
EMBEDDING_DIMENSIONS=384   # MUST match the model's native dim

# On the server
docker exec cortexplexus-local-ollama ollama pull snowflake-arctic-embed:s
docker compose restart cortexplexus

# From any MCP client (clears file-hash cache so agent re-embeds everything)
force_reindex(name="<your-repo-name>")
```

### Parallelism (why it doesn't help on Ollama)

`MaxParallelBatches` defaults to `1` for Ollama, `4` for Gemini. Do **not** raise the Ollama default — three independent measurements (R17 on LXC, v0.9.0 Wave 1 on LXC, v0.9.0 Wave 1 on local PC) showed parallel=4 gives identical wall-time to parallel=1 on `nomic-embed-text`, because Ollama's model loader is effectively single-threaded for CPU inference. Raising parallelism just wastes CPU context-switches without speedup. Gemini's rate-limit is per-request-count, so parallelism IS free throughput there.

### Other faster options

1. **Switch to Google Gemini** — set `EMBEDDING_PROVIDER=gemini` and `GEMINI_API_KEY` in `.env`. Free tier handles ~50 RPM, paid tier scales linearly.
2. **Run Ollama on a GPU host** — same model, 5–10× faster.
3. **Pause other watch agents during bulk re-index** — if you run multiple agents from one machine, their concurrent embedding upload contends on the same Ollama instance. Measured cost of concurrent CortexFlow+CortexPlexus watch: ~40% slowdown on parallel-4 scenarios (v0.9.0 Wave 1 finding).

### Infrastructure health matters more than code

Before blaming the embedding provider, check the host where Ollama runs:

```bash
ssh user@host 'uptime; free -h; top -bn1 | head -10'
```

High load average + high `%wa` (iowait) means neighbor workloads are stealing the I/O subsystem — no code change will recover that. v0.9.0's driving finding was that a contested LXC was 6× slower than its own R17 baseline, with zero code or config drift.

The HNSW bulk-load optimization (drop / rebuild for batches ≥ 500 symbols) handles the vector-store side; the bottleneck is purely the embedding provider's throughput.

## Re-index after fixing a server-side bug

If a server bug (e.g. an issue-#1 regression) caused vectors to silently drop, **the file hashes still got persisted**, so the agent will think every file is up to date and skip everything on the next run. Use `force_reindex(name)` to clear the server-side hash cache, then re-run the agent:

```bash
# From any MCP client:
force_reindex(name="MySolution")

# Then:
dotnet cortexplexus-agent index "/path/to/MySolution" --server http://… --name MySolution
```

## Multiple projects, one agent host

The agent can supervise multiple projects from one machine — each `--name` gets its own PID file under `~/.cortexplexus/agents/<name>.pid`, so `agent status` lists all of them and `agent stop --all` shuts them down cleanly. For auto-start of multiple projects via systemd, see [`agent-auto-start.md`](agent-auto-start.md) (the unit is parameterized by `<name>` instance).

## See also

- [`docs/HEALTH-METRICS.md`](../HEALTH-METRICS.md) — interpret the `list_repositories` Health line
- [`runbooks/agent-auto-start.md`](agent-auto-start.md) — survive reboots
- [`runbooks/maintenance.md`](maintenance.md) — disk + Docker housekeeping
- [`runbooks/deployment.md`](deployment.md) — initial CortexPlexus stack deploy
