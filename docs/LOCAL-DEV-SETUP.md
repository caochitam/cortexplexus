# Local dev setup — CortexPlexus on your PC

This repo ships two independent environments. Know which one you're touching.

| Environment | Where | Purpose | Who uses it |
|-------------|-------|---------|-------------|
| **Release** | LXC `192.168.50.14` | Production workload; the indexing store users actually query via MCP | Runtime only. Never benchmark on it. |
| **Local** | This PC (Docker) | Benchmarks, experiments, Wave-work | Everything that isn't a release-ready change |

The LXC is shared infrastructure with I/O contention from neighbor VMs on its Proxmox host — benchmarking there mixes our signal with unpredictable noise (measured 2026-04-19, see memory `25623bbd`). All performance work happens on local.

## Port map

| Service | Local (Docker) | LXC (release) | Notes |
|---------|----------------|----------------|-------|
| Ollama | `http://localhost:11434` | `http://192.168.50.14:11434` | same port — no conflict because LXC is remote |
| Postgres | `localhost:15432` | `192.168.50.14:5432` | **offset +10000** on local to avoid any host Postgres |
| CortexPlexus App (HTTP + MCP) | `http://localhost:18080` | `http://192.168.50.14:8080` | **offset +10000** — future work, not yet wired |

Credentials (local only, safe to commit):
- Postgres: `cortexplexus / cortexplexus`
- Ollama: no auth

## Stack file

[`local-dev/docker-compose.yml`](../local-dev/docker-compose.yml) — lean compose with two services:

- **`ollama`** (default): pinned to `ollama/ollama:0.20.0` (matches LXC release). `nomic-embed-text` gets pulled on first benchmark run.
- **`postgres`** (profile `full-stack`): only starts when you explicitly opt in; most work doesn't need it.

## Quick-start (Ollama only — for benchmarking)

```bash
cd local-dev
docker compose up -d ollama
docker exec cortexplexus-local-ollama ollama pull nomic-embed-text
curl -sS http://localhost:11434/api/version      # {"version":"0.20.0"}
```

## Quick-start (full stack — Postgres + Ollama)

```bash
cd local-dev
docker compose --profile full-stack up -d
```

Postgres data volume `cortexplexus-local-pgdata` is separate from anything on the LXC and from any future prod Postgres on this machine. Nuke with `docker compose --profile full-stack down -v`.

## Running the benchmark harness against local

```bash
dotnet build tests/CortexPlexus.Embedding.Benchmarks
dotnet run --project tests/CortexPlexus.Embedding.Benchmarks -- \
  --ollama-url http://localhost:11434 \
  --repro-r17 \
  --out docs/benchmark-results/local-$(date +%Y%m%d).md
```

Expected on this PC (2026-04-19 baseline, Ollama 0.20.0 in Docker):

| Scenario | Median | Throughput |
|----------|--------|------------|
| batch=50, parallel=1, n=50 | ~2.4s | ~21 texts/s |
| batch=200, parallel=1, n=200 | ~9.4s | ~21 texts/s |
| batch=50, parallel=4, n=200 | ~9.4s | ~21 texts/s |

Notes:
- The first run of each scenario is ~1.5s slower than subsequent runs (cold model load). Median of 3 repeats cancels this out.
- Parallel 4 ≈ Parallel 1 on `nomic-embed-text` — R17's "client-side parallelism = 0 speedup for Ollama" finding continues to hold. Do not try Candidate E unless a future model inversely proves it.

## Why these numbers differ from R17

R17 (2026-04-10) measured ~29 texts/s on the LXC when it had ~381 MiB free RAM and low neighbor load. Current local PC measures ~21 texts/s (1.4× slower than R17). The delta is inside "different-hardware noise" — your PC CPU is not the same as the R17 test host. The LXC today measures ~4.6 texts/s (6× slower than R17) due to Proxmox host I/O contention, not inherent LXC capacity.

Do **not** use LXC numbers for any perf claim. They are infrastructure-contention-bound, not code-bound.

## What NOT to run on local

- `docker compose up -d` from the repo root — that pulls the production images and would try to bind port 8080, which the local dev tree assumes is free for the future local App. Either use `local-dev/` stack, or know what you're doing.
- Production deploy commands (`bash deploy.sh`) — those target the LXC. Never run them pointing at local.

## Stopping / reset

```bash
cd local-dev
docker compose stop                           # pause, keep volumes
docker compose down                           # remove containers, keep volumes
docker compose --profile full-stack down -v   # nuke everything, including pgdata + model cache
```
