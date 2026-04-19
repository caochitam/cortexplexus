# CortexPlexus.Embedding.Benchmarks

Throughput benchmark harness for Ollama embedding. Drives **Wave 1 of v0.9.0** — see
[`docs/PLAN-v0.9.0-embedding-throughput.md`](../../docs/PLAN-v0.9.0-embedding-throughput.md).

## What it does

Issues real HTTP calls to an Ollama endpoint (LXC or localhost), varying:

- **Model** (`nomic-embed-text` baseline + candidates)
- **Batch size** (how many strings per `/api/embed` call)
- **Client parallelism** (how many concurrent HTTP calls)

For each scenario it runs 3 times, reports the median wall time, and emits a markdown
table ready to paste into [`docs/BENCHMARK.md`](../../docs/BENCHMARK.md).

**Not a test** — do **not** run from `dotnet test`. Build + invoke directly.

## Build

```bash
dotnet build tests/CortexPlexus.Embedding.Benchmarks/CortexPlexus.Embedding.Benchmarks.csproj
```

## Run modes

### R17 reproduction (gate-1 sanity check — run this first)

```bash
dotnet run --project tests/CortexPlexus.Embedding.Benchmarks -- --repro-r17 \
  --ollama-url http://192.168.50.14:11434 \
  --out docs/benchmark-results/repro-r17-$(date +%Y%m%d).md
```

Runs three scenarios from
[`BENCHMARK.md` §R17](../../docs/BENCHMARK.md) (1×50, 1×200, 4×50 parallel).
Prints a per-scenario comparison: `WITHIN 20% / SLOWER / FASTER` vs R17's numbers.

**Decision gate**:

- All three **WITHIN 20%** → Ollama/LXC unchanged since R17, trust the harness,
  proceed to full sweep.
- Any scenario **deviates** → stop. Check Ollama version, model, concurrent
  workload, LXC load before running the full sweep. Update the PLAN if the
  baseline itself changed.

### Full sweep

```bash
dotnet run --project tests/CortexPlexus.Embedding.Benchmarks -- \
  --ollama-url http://192.168.50.14:11434 \
  --models nomic-embed-text,mxbai-embed-large,all-minilm,snowflake-arctic-embed \
  --batch-sizes 25,50,100,200,500 \
  --parallelism 1,2,4 \
  --corpus-size 2000 \
  --out docs/benchmark-results/sweep-$(date +%Y%m%d).md
```

60 scenarios × 3 repeats. Estimated wall time: 30–60 min depending on Ollama
throughput and whether any model OOMs at large batches.

Before running: stop concurrent watch agents to isolate the Ollama capacity
under test.

```bash
dotnet "$USERPROFILE/.cortexplexus/agent/cortexplexus-agent.dll" stop --all
```

### Custom / quick probe

```bash
dotnet run --project tests/CortexPlexus.Embedding.Benchmarks -- \
  --ollama-url http://localhost:11434 \
  --batch-sizes 50 --parallelism 1 --corpus-size 200
```

For a one-off spot check during development.

## Output

```
## Ollama embedding benchmark — 2026-04-19 14:30Z

- Endpoint: `http://192.168.50.14:11434`
- Corpus: 2000 synthetic code-like strings, seed 42
- Repeats per scenario: 3 (median reported)
- Mode: full sweep

| Model | Batch | Parallel | Total texts | Median wall | Throughput (texts/s) | Errors |
|-------|------:|---------:|------------:|------------:|---------------------:|-------:|
| nomic-embed-text | 50 | 1 | 2000 | 68.54s | 29.2 | 0 |
| nomic-embed-text | 100 | 1 | 2000 | 67.20s | 29.8 | 0 |
| ...
```

stdout gets the markdown; stderr gets per-run progress lines + the R17 verdict.

## Interpreting results

Follow the decision matrix in [PLAN §4](../../docs/PLAN-v0.9.0-embedding-throughput.md#4-wave-2--apply-the-one-change-that-measurement-justifies).
Only one candidate ships per release, and only if it shows **≥ 1.3× measured** improvement.

If nothing clears 1.3× — that's a valid v0.9.0 outcome: ship the harness + the
updated benchmark numbers + the doc refresh. A negative result rigorously measured
is still a release.

## Corpus determinism

Default seed is `42`. Same seed + same corpus size → same strings every run.
Change only via `--corpus-size` (seed is fixed in code). Embeddings are
discarded — we measure only wall time + error count.
