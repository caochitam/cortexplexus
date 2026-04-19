# PLAN v0.9.0 — Embedding Throughput: Measure First, Then Tune

**Status:** Draft. Awaiting sign-off before Wave 1 work starts.
**Owner:** DT-Tuan + Claude
**Date:** 2026-04-19
**Successor-of:** R17/R18/R19 embedding work (see `docs/BENCHMARK.md` Rounds 17-19)

---

## 1. Context and goal

The v0.8.4 re-index of CortexPlexus measured **~7–8 embeddings/sec** end-to-end on the LXC server (LocalIndexer → HTTP POST /api/index/results → server-side EmbeddingBatchHelper → Ollama). A full 3,185-symbol re-index took **1,479 seconds (~25 min)**, with the three symbol chunks alone accounting for ~775s (Ollama time).

R17 ground truth on the same hardware measured **~29 texts/s** peak direct Ollama throughput — a **4× gap** between isolated Ollama benchmark and production re-index. That gap is the entire thesis of this release: either we are leaving 4× on the table because of how the helper batches/sends work to Ollama, or the LXC has degraded since R17, or a concurrent workload eats Ollama capacity. We do not yet know which.

**Goal of v0.9.0:** close as much of the 4× gap as measurement supports. Stretch: absolute speedup (new model / sharded Ollama / batch-size tuning) beyond the 4× closing.

**Non-goals:**
- Replacing Ollama with a different inference backend (vLLM, TEI). Out of scope for this release — logged for v1.0.
- Changing embedding dimensions or the stored schema.
- Switching default provider to Gemini. Documentation may recommend it; the code default stays Ollama.

---

## 2. What we already know (and what we don't)

### Established (do not re-measure)

- **R17 on the same LXC, 2026-04-10**: Ollama + `nomic-embed-text` is ~29 texts/s, **client-side parallelism gives 0 speedup**, setting `OLLAMA_NUM_PARALLEL=4` had no effect because `nomic-embed-text` lacks KV caching. Reference: `docs/BENCHMARK.md:1525`.
- **R19 landed auto-defaults**: `MaxParallelBatches=1` for Ollama, `=4` for Gemini. Source: `EmbeddingOptions.cs:27`, auto-detection in `ServiceCollectionExtensions.ApplyProviderDefaults`.
- **Parallel.ForEachAsync infrastructure shipped in R17**: `EmbeddingBatchHelper.cs` already runs batches concurrently when MaxParallelBatches > 1; per-batch exception isolation is in place.
- **`.slnx` gotcha**: saved as memory `8b526d94` — always iterate per-project for test coverage; do not trust `dotnet test CortexPlexus.slnx` for full matrix.

### Open questions (v0.9.0 will answer)

| # | Question | Why it matters |
|---|----------|----------------|
| Q1 | Why is production 7–8/s vs R17's 29/s on identical hardware? | Determines whether this is a config / codepath bug or a real capacity change |
| Q2 | Does `EmbeddingBatchHelper`'s hardcoded `BatchSize = 50` miss a win the `EmbeddingOptions.MaxBatchSize = 100` setting promises? | Codepath bug — the option is not wired through |
| Q3 | Has Ollama (or the model) been updated since R17? Does the new version support true batching? | Would invalidate R17's "parallelism = 0 speedup" finding |
| Q4 | Is concurrent indexing (e.g. CortexFlow watch mode) the reason production is 4× slower? | Affects what default we recommend users set for multi-repo workloads |
| Q5 | Is there a model available today that gives 2–5× on the same CPU (e.g. `mxbai-embed-large`, `all-minilm`, `snowflake-arctic-embed`)? | Lowest-effort way to ship measurable improvement |

---

## 3. Wave 1 — Measurement harness (the gating investment)

**Scope**: build a repeatable, version-controlled benchmark that any future release can re-run to reason about throughput regressions. Not a one-off script.

### Deliverable

- `tests/CortexPlexus.Embedding.Benchmarks/` — new csproj using BenchmarkDotNet OR a minimal custom harness (decision below in §3.2).
- Runs against a configurable `OllamaBaseUrl` (local or LXC), reports per-scenario throughput in a consistent markdown table compatible with `docs/BENCHMARK.md`.

### 3.1 Scenarios to cover

Matrix to sweep (measure on the LXC, isolated — no concurrent CortexFlow watch):

| Dimension | Values |
|-----------|--------|
| Model | `nomic-embed-text` (baseline), `mxbai-embed-large`, `all-minilm`, `snowflake-arctic-embed` |
| Batch size (single HTTP call) | 25, 50, 100, 200, 500 |
| Client parallelism | 1, 2, 4 |
| Corpus | Fixed synthetic corpus of 2,000 code-like strings (avg 200 chars, deterministic) |

That's **4 models × 5 batches × 3 parallel = 60 runs.** Each run embeds 2,000 strings and reports: wall time, total embeddings, effective texts/s, p50/p99 per-batch latency, HTTP error count.

Skip cells where model refuses (e.g. `all-minilm` OOM at batch 500) — record the failure explicitly.

### 3.2 Harness choice

**Recommendation:** custom harness, ~200 LoC, not BenchmarkDotNet. Reasons:
- BDN is great for nanosecond-level CPU measurement. For multi-second HTTP-bound throughput it adds ceremony without benefit.
- Output format needs to be markdown-table-shaped for CHANGELOG, not CSV/JSON.
- We only have 60 runs; no need for statistical rigor beyond "3 runs median".

Rejected alternative: pure bash script. Too hard to parse JSON / compute percentiles portably across Windows dev + Linux LXC.

### 3.3 Baseline to reproduce first

Before the sweep, reproduce R17's numbers exactly as a sanity check:

| R17 scenario | Expected | If observed < expected |
|--------------|----------|------------------------|
| 1 req × 50 texts, `nomic-embed-text` | 1.82s | Ollama / LXC has degraded since R17; note in findings |
| 1 req × 200 texts | 6.85s | Same |
| 4 parallel × 50 texts | 6.91s | Same (or Ollama now supports parallelism — investigate) |

If R17 reproduces: the 4× gap is in our code/pipeline, not Ollama. Wave 2 focuses there. If R17 does not reproduce: Ollama / hardware has changed, re-baseline and the sweep above tells us new peak.

### 3.4 Done criteria for Wave 1

- Benchmarks csproj compiles, checked in, documented in `docs/BENCHMARK.md` under a new **"Round 26 — v0.9.0 embedding harness"** section.
- One full sweep run on LXC, results committed as a markdown table.
- At least one candidate improvement identified with measured upside.

---

## 4. Wave 2 — Apply the one-change that measurement justifies

No code change ships in Wave 1. Wave 2's work is a function of Wave 1 findings. Below are the **candidate changes, pre-ranked by implementation cost**. Pick exactly one (or "do nothing" if nothing clears the gate).

### Candidate A — Wire `EmbeddingOptions.MaxBatchSize` through to the helper (1-line change)

Today `EmbeddingBatchHelper.BatchSize` is `const int = 50;`. `EmbeddingOptions.MaxBatchSize` exists but is not read. If measurement shows batch 100 is ≥ 1.3× batch 50 at equal parallelism, change helper to read the option. **Minimum viable fix.** Keep default 50 or bump to 100.

### Candidate B — Model swap recommendation (doc-only, zero code)

If a different Ollama model gives ≥ 1.5× at equal quality, update `docs/runbooks/agent-best-practices.md:74` to recommend it and add a migration note. Do not change the default — users on `nomic-embed-text` keep their embeddings' compatibility.

### Candidate C — Measure and document the "concurrent watch" penalty

If running CortexFlow watch concurrently degrades CortexPlexus indexing by ~4×, document the "two-watchers-same-server" scenario in the best-practices runbook with a concrete recommendation (e.g. "pause one watch during bulk re-index").

### Candidate D — Sharded Ollama (reject by default)

Running 2× Ollama containers in docker-compose behind a round-robin proxy. Non-trivial: proxy config, health checks, model-already-loaded warm-up, docker-compose surface. **Only pursue if Wave 1 shows no other candidate clears 1.5×.**

### Candidate E — Raise parallelism default despite R17 (reject unless Wave 1 inverts R17)

Only if Wave 1 finds Ollama has updated to genuinely support concurrent requests on `nomic-embed-text`. Otherwise this is the exact trap R17 already corrected — do not repeat.

---

## 5. Wave 3 — Document + ship

- Update `docs/BENCHMARK.md` with Round 26 full results (table format consistent with R17–R25 rounds).
- Update `docs/runbooks/agent-best-practices.md` §Tune the embedding throughput with the new data-backed numbers, replacing the current "~4–5 emb/sec" estimate.
- If Candidate A or D shipped: update CHANGELOG `[Unreleased]` with measured before/after.
- If only Candidate B or C shipped: this is a v0.9.0 doc release. That is fine — an honest "we measured, pattern held, here's the updated guidance" is worth a minor version bump.
- Tag `v0.9.0`. Deploy to LXC.

---

## 6. Decision gates (do not skip)

- **After Wave 1 §3.3 (R17 repro)** — stop and update this plan if R17 does not reproduce. Numbers change → Wave 2 candidates re-rank.
- **Before Wave 2 code change** — the selected candidate must show measured ≥ 1.3× improvement on the benchmark, not projected. Follow the memory `"Measure Before Projecting"` (e8c6de55).
- **Before tagging v0.9.0** — full test matrix green via per-project iteration (see memory `8b526d94`), `prepush-verify` skill or equivalent.

---

## 7. Risk register

| Risk | Mitigation |
|------|-----------|
| LXC has degraded since R17 (CPU thermal, other VMs competing) | Wave 1 §3.3 catches it. Update plan if so. |
| Model swap breaks existing embeddings | Migration note; do not change default. Only opt-in. |
| Benchmark harness itself is wrong → false conclusions | Reproduce R17 first; if those numbers match, trust the harness. If not, fix harness before trusting new scenarios. |
| Sharded Ollama introduces proxy complexity that outweighs gain | Stay rejected unless no other path. |
| Yak-shaving the harness while users wait for a 1-line fix | §3 has a cost ceiling; if §3.3 shows Candidate A wins already, ship it — skip full sweep. |

---

## 8. Out of scope for v0.9.0

- Alternative inference backends (vLLM / TEI / LM-deployed embedding). v1.0.
- Chunking strategy for long symbol bodies. Tracked separately.
- GPU-accelerated embedding on the LXC. Hardware ask, not software.
- Automatic backpressure when Ollama saturates. Requires circuit-breaker design.

---

## 9. Dependencies / pre-requisites

- LXC accessible at `192.168.50.14:11434` for Ollama endpoint (confirm via Wave 1 §3.3).
- Ability to pull additional Ollama models on the LXC (disk + network). Estimate: 2–4 GB per candidate model.
- No concurrent watch agents while Wave 1 sweep runs. Coordinate pause via `dotnet cortexplexus-agent stop --all`.

---

## 10. Sign-off checklist

- [x] User confirms "measure first, ship only what measurement supports" approach. (2026-04-19)
- [x] User OKs spending ~2-3h on Wave 1 harness + ~30 min runtime for full sweep before any code change.
- [x] User confirms scope — no model swap as default, no inference-backend swap.

---

## 11. Actual outcome — Wave 1 and Wave 2 log (2026-04-19 → 04-20)

### Wave 1 — measurement harness + gate 1

**Shipped**: `tests/CortexPlexus.Embedding.Benchmarks/` (custom harness, ~200 LoC C#, markdown output). Committed `d20075f`.

**R17 repro on LXC**: **FAILED gate** — all 3 scenarios ~6× slower than R17 (4.6 texts/s vs 29/s). Isolating watch agents removed only the parallel-4 contention noise; baseline regression remained. RAM upgrade 1 GiB → 2 GiB on LXC did not recover. Root cause diagnosed via `top -bn1` + `iostat`: persistent **67.7% CPU iowait with 0% user CPU and ~10% disk util** — classic signature of Proxmox-host-level I/O contention from neighbor VMs, outside the LXC's control.

**Pivot**: split local-dev env from release env. `local-dev/docker-compose.yml` runs Ollama 0.20.0 (same image as LXC) on this PC. R17 repro on local measured ~21 texts/s — 1.4× slower than R17 (hardware-noise band), ~4.5× faster than the contested LXC. **Local is now the benchmark environment; LXC is release-only.** Committed `f5fcf28`. See `docs/LOCAL-DEV-SETUP.md`.

### Wave 2 — Candidate B model sweep

**Ran**: 4 models × batch 100 × parallel 1 × 500 texts on local. Results in `docs/benchmark-results/model-sweep-20260420.md`:

| Model | Dim | texts/s | vs default |
|-------|----:|--------:|-----------:|
| `nomic-embed-text` (default) | 768 | 20.9 | 1.0× |
| `mxbai-embed-large` | 1024 | 5.6 | **0.27× — avoid** |
| `snowflake-arctic-embed:s` | 384 | 56.2 | **2.7× ✅** |
| `all-minilm` | 384 | 101.9 | **4.9× ✅** |

**Decision**: ship Candidate B as **doc-only recommendation** in `docs/runbooks/agent-best-practices.md`. `snowflake-arctic-embed:s` gets the primary recommendation (2.7× faster + retrieval-tuned); `all-minilm` as a secondary option for max-throughput-over-quality workloads. `nomic-embed-text` remains the default — existing deployments should only switch if they accept a one-time force-reindex (384-dim ≠ 768-dim). `mxbai-embed-large` explicitly disqualified.

### Candidate status summary

| Candidate | Plan verdict | Actual result |
|-----------|-------------|---------------|
| A — wire `MaxBatchSize` through | considered if ≥1.3× at batch 100 | **REJECT** — batch 50 vs 200 both 21/s on local (0× gain) |
| B — model swap doc | considered if new model ≥1.5× | **SHIP** — snowflake-arctic 2.7×, all-minilm 4.9× |
| C — document concurrent-watch penalty | considered if measured | **SHIP** — confirmed ~18s overhead on parallel=4 scenarios |
| D — sharded Ollama | rejected unless nothing else works | **still rejected** — B clears the gate |
| E — raise parallelism default | rejected unless R17 inverts | **confirmed rejected** — 3 independent repros show 0 speedup |

### Wave 3 — what v0.9.0 actually ships

- Benchmark harness (already committed)
- Split dev/release env + `docs/LOCAL-DEV-SETUP.md` (already committed)
- Runbook rewrite: `docs/runbooks/agent-best-practices.md` §Tune embedding throughput with measured table + recommendations + anti-patterns
- Updated this PLAN with §11 outcome log
- Tag v0.9.0 once the runbook + this PLAN are reviewed

**No app code change ships in v0.9.0.** That is correct — measurement showed none was justified. The diagnostic + operational guidance is the deliverable.
