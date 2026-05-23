# ECC-Inspired Feature Candidates (Level B)

> **Status: proposals, not committed roadmap.** Distilled from analysing
> [`affaan-m/everything-claude-code`](https://github.com/affaan-m/everything-claude-code)
> (ECC) during the R27 session (2026-05-22). These are *product* features for
> CortexPlexus to learn from ECC — distinct from "Level A", which was just using
> ECC patterns as working templates for the R27 bug-fix execution.
>
> Nothing here is implemented. Each item needs its own `/clarify` + ADR before work starts.

## Why this exists

While fixing R27 we cloned ECC and mined it for reusable patterns. Some patterns
are worth *building into* CortexPlexus, not just borrowing for one session. This
doc captures those so the ideas aren't lost. The five ECC operating principles
(research-first, context-as-gold, hook-discipline, scoped-delegation,
no-premature-abstraction) are already saved as a global memory and applied to how
we work; the items below are about the *product surface*.

## Candidates

### B1 — Instinct Feedback Loop → MCP tool  ·  Priority P1
- **ECC source:** `commands/instinct-export.md`, `instinct-import.md`, `instinct-status.md`
- **Pattern:** a YAML "instinct" store (id, trigger, confidence, domain, scope),
  dedupe-merge by confidence, project + global scope hierarchy with project override.
- **CortexPlexus mapping:** 3 MCP tools — `instinct_save`, `instinct_recall`,
  `instinct_status` — persisting to `~/.cortexplexus/instincts/<repo>/`, integrated
  with the existing memory subsystem (`Memory__Enabled`). Lets a team capture and
  share repeatable findings across projects.
- **Value:** realises the deferred "idea #3" (structured smoke-test/finding capture)
  the user originally flagged. The R27 session itself produced a candidate instinct
  (see below).
- **Effort:** M (~2–3 days: schema, 3 tools, persistence, tests). **Deps:** none.

### B2 — Database Review MCP tool  ·  Priority P2
- **ECC source:** `agents/database-reviewer.md` (Postgres specialist).
- **Pattern:** structured probes — `pg_stat_statements`, lock contention, index
  health, FK coverage — emitting tiered findings.
- **CortexPlexus mapping:** `mcp__cortexplexus__database_review` running probes
  against the project's Postgres (and usable as self-diagnostic of the CortexPlexus
  stack itself). The Apache AGE / Npgsql connection plumbing already exists.
- **Value:** R27-0 had to be done by hand; a tool would automate FK/lock/index
  audits next time. (Note: R27-1's actual root cause was in C#, not the DB — so this
  would *complement*, not replace, static analysis.)
- **Effort:** M (probe templates on existing connector). **Deps:** project DB connection in MCP context.

### B3 — Benchmark Skill v2 (before/after Mode 4)  ·  Priority P1
- **ECC source:** `skills/benchmark/SKILL.md` Mode 4.
- **Pattern:** git-tracked JSON baselines under `.cortexplexus/benchmarks/`;
  a `benchmark compare` command emits a delta table with an automatic PASS/FAIL verdict.
- **CortexPlexus mapping:** promote `tests/CortexPlexus.Embedding.Benchmarks/` to a
  first-class CLI subcommand. Each round (R17…R27) gets a baseline JSON; reruns
  auto-compare.
- **Value:** today every round is hand-written into `docs/BENCHMARK.md` (per the
  "update BENCHMARK" rule). This automates the comparison and removes a manual,
  error-prone step.
- **Effort:** S–M (harness exists; add baseline persistence + compare). **Deps:** stable baseline schema.

### B4 — Tiered Code-Review MCP tool  ·  Priority P3
- **ECC source:** `agents/code-reviewer.md` + `csharp-reviewer.md`.
- **Pattern:** 4-gate pre-filter (cite exact line, concrete failure mode, read
  surrounding context, defensible severity); tiered output CRITICAL/HIGH/MEDIUM/LOW;
  verdict APPROVE/WARNING/BLOCK.
- **CortexPlexus mapping:** `mcp__cortexplexus__review_diff <branch>`, composing the
  existing dead-code / impact-analysis / circular-dependency tools.
- **Value:** R27 used ad-hoc review subagents (worked well — both APPROVE). A
  dedicated tool would make this consistent and repo-aware.
- **Effort:** M–L (diff parse + integrate analyzers). **Deps:** B2 + existing graph tools.

### B5 — Hook system for the indexing pipeline  ·  Priority P2
- **ECC source:** `hooks/hooks.json` (PreToolUse / PostToolUse / SessionStart / Stop).
- **Pattern:** trigger-based validation/feedback hooks — never general-purpose logic.
- **CortexPlexus mapping:** PreIndex (validate workspace, check FK schema), PostIndex
  (auto-update BENCHMARK, save instinct candidates), Stop (audit incomplete batches —
  e.g. flag the R27-1 FK drops loudly).
- **Value:** an audit hook would have surfaced R27-1's silent batch drops at run time.
  Pairs naturally with the fail-loud filter shipped in v0.9.1.
- **Effort:** M (hook framework in the .NET app). **Deps:** hook framework design.

### B6 — Subagent template library  ·  Priority P3
- **ECC source:** `agents/code-explorer.md`, `csharp-reviewer.md`, et al.
- **Pattern:** structured brief format (entry point, probes, output schema, gates).
- **CortexPlexus mapping:** ship adapted templates under `docs/agent-templates/` for
  CortexPlexus users to drop into their own projects.
- **Value:** low-effort readability win; defer until there's a real user base.
- **Effort:** S (copy-adapt; check ECC license first). **Deps:** ECC license review.

## Priority summary

| # | Feature | Priority | Rationale |
|---|---------|----------|-----------|
| B3 | Benchmark v2 | **P1** | Automates a manual per-round chore we do today |
| B1 | Instinct loop | **P1** | Realises the deferred idea #3; user already interested |
| B2 | DB review tool | P2 | Useful but standalone value modest |
| B5 | Indexing hooks | P2 | Good safety net; infra-heavy |
| B4 | Tiered review tool | P3 | Wait for larger user base |
| B6 | Agent templates | P3 | Low effort, low urgency |

## Candidate instinct from the R27 session

Worth capturing if/when B1 lands — and useful as a working principle now:

> **Trigger:** a bug is attributed to a runtime/concurrency cause (race, timing,
> contention) in notes or memory, but hasn't been confirmed against the code.
> **Instinct:** read the actual code path first — `/clarify` before fixing. In R27
> *both* memory hypotheses were wrong (R27-1 "race" was a `switch` fall-through;
> R27-2 "oversize text" was an FQN-collapse miscount). Static analysis found both in
> minutes; a stack spin-up would have cost hours and still needed the code read.
> **Confidence:** high (two-for-two this round, consistent with the R17 "measure
> before projecting" lesson).
