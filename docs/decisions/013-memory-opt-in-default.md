# ADR-013: Memory system is opt-in — default disabled

**Status:** Accepted
**Date:** 2026-04-17

## Context

The v0.8.0 agent memory system stores free-form text supplied by AI agents. Content can include:

- User preferences ("prefers terse answers")
- Project conventions
- Bug notes
- Occasionally: accidentally-embedded secrets, PII, or sensitive context

Even with `ISecretsScanner` rejecting obvious patterns (tokens, API keys, emails), the store accumulates user-visible content that:

- Persists across sessions
- Is retrievable semantically (so it can resurface unexpectedly)
- Travels with `pgdata` backups / images

CortexPlexus users range from hobbyists to security-sensitive enterprises. The feature delivers value only when the user explicitly asks for it — silent collection is surprise data collection, which damages trust.

## Decision

**Memory is disabled by default. Users must opt in via config.**

### Default state

```json
{
  "Memory": {
    "Enabled": false
  }
}
```

### All 4 MCP tools check the gate at entry

```
save_memory     → "Memory is disabled. Enable via Memory.Enabled in appsettings.json or Memory__Enabled=true. See docs/MEMORY-SYSTEM.md."
recall_memory   → same
list_memories   → same
forget_memory   → same
```

Returning an error (not silent success, not a stub) makes the disabled state visible: AI agents see the error, relay it to the user, the user decides whether to enable.

### `list_repositories` advertises the state

Adds a one-line suffix: `Memory: disabled` or `Memory: enabled (N items)`.

## Why opt-in

1. **Surprise data collection is a trust violation.** A fresh deploy of v0.8.0 should behave identically to v0.7.1 unless the user opts in. Defaulting to enabled would silently start persisting free-form agent text on every deployment, including existing production instances that update.

2. **Data residency is the user's concern, not ours.** Different users have different rules about where conversation-derived content can live. Opt-in lets compliance-sensitive users deploy v0.8.0 safely without having to audit what's being stored.

3. **Upgrade safety.** Existing users running `docker compose pull` from v0.7.1 → v0.8.0 must not suddenly find a new data store populated. Opt-in guarantees the upgrade is invisible unless requested.

4. **Feature cost is real.** Even disabled, the table exists and the reaper background service starts. Enabling is reversible; the default stays conservative.

## Rejected alternatives

### Default enabled with a prominent `--disable-memory` flag

- Pros: users get value out of the box.
- Cons: see "surprise data collection" — enterprise users can't use v0.8.0 without manually disabling first, which is the opposite of safe defaults.
- **Why rejected**: trust hit outweighs convenience for new users, who pay only one `Memory__Enabled=true` line.

### Per-call flag (every `save_memory` takes `persist: bool`)

- Pros: fine-grained control.
- Cons: friction on every save; AI agents will forget to set it; confusing semantics for `recall_memory` (what does "persist" mean for a read?).
- **Why rejected**: pushes a global decision (do I want memory at all?) to per-call decisions (do I want *this* saved?). Wrong level of abstraction.

### Per-project flag in `.cortexplexusignore`

- Pros: fits the existing per-repo config pattern.
- Cons: memory also has `global` scope which isn't project-local; doesn't address the fresh-deploy trust concern (the flag default still has to be chosen).
- **Why rejected**: moves the opt-in decision one level deeper without fixing the default-state question.

### Prompt for consent on first tool call

- Pros: interactive, explicit consent.
- Cons: MCP is a stateless RPC transport; no UI plumbing for prompts; blocks the AI's workflow.
- **Why rejected**: infeasible given MCP's design.

## Consequences

### Positive

- Fresh v0.8.0 deploy behaves identically to v0.7.1 until opt-in.
- Users who don't enable pay zero storage cost (table has zero rows, reaper no-ops).
- Disabling later is reversible: flag off → tools block calls but data stays. Flag on → everything comes back.

### Negative / risks

- **Discoverability** — new users won't know memory exists unless they read docs or see `Memory: disabled` in `list_repositories`. Mitigation: README mentions it; MCP-GUIDE lists tools with "(disabled by default)".
- **No analytics** — we can't measure how many users enable the feature unless they self-report. Acceptable; we prioritise privacy over telemetry.

## Follow-ups

- If user feedback shows the opt-in friction is causing the feature to be underused despite interest, revisit the default in v0.9.0 with a migration notice.
- Consider auditing (append-only log of save/forget calls) if an enterprise user requests it — defer until asked.
