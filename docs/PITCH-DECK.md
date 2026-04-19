# CortexPlexus — Pitch Deck Outline (8 slides)

> Copy each slide block into Keynote, Google Slides, or Marp. Text is pre-trimmed
> so a single slide fits 1-2 min of talking time. Speaker notes in *italics*.

---

## Slide 1 — Title

**CortexPlexus**
*Turn your source code into a Knowledge Graph for AI*

Open-source. Self-hosted. Free forever.

*Speaker: "CortexPlexus is a free, self-hosted platform that lets any AI coding assistant — Claude Code, Cursor, VS Code Copilot — actually understand your codebase instead of grepping through it."*

---

## Slide 2 — The problem (one slide, one chart)

**AI agents read code as plain text. That's the problem.**

To answer "what calls `ProcessOrder`?", today's agent does:
1. `grep -r "ProcessOrder" ./src` (gets 47 hits)
2. Read 10 files to filter noise
3. Read their callers
4. Manually assemble a call graph
5. Still miss runtime DI wiring, EF Core lazy loading, middleware…

→ **10+ tool calls per question. Tens of thousands of wasted tokens. Often wrong.**

*Speaker: "Every developer using AI tools has felt this. You ask a simple structural question and the agent takes 30 seconds and $0.20 in tokens — and still gets it wrong."*

---

## Slide 3 — The solution

```
┌────────────────────────────────┐
│   Your AI agent (Claude etc.)  │
└──────────────┬─────────────────┘
               │  MCP (1 HTTP call)
               ▼
┌────────────────────────────────┐
│        CortexPlexus             │
│  Roslyn + Tree-sitter parsers   │
│  Knowledge Graph (AGE)          │
│  Vector search (pgvector HNSW)  │
│  Full-text (tsvector BM25)      │
│  → 26 structured MCP tools      │
└────────────────────────────────┘
```

**One tool call. Structured answer. Under 200 ms typical.**

*Speaker: "Two Docker containers. One graph DB. Twenty-six tools. Works with any MCP-compatible client."*

---

## Slide 4 — Three numbers that matter

| | Before | With CortexPlexus |
|---|---|---|
| Agent calls per "understand this service" | 15+ | **1** |
| HNSW vector indexing speed (R18 bench) | 51 min | **5.5 sec** |
| Annual cost for 20 devs | $5,000–$10,000 | **$0** |

*Speaker: "The HNSW benchmark is real — that's a 556× speedup from our R18 release, measured end-to-end on pgvector pg17 with 500+ symbol batches. The cost number is what GitHub Copilot Business or Sourcegraph Enterprise actually charge."*

---

## Slide 5 — Six concrete use cases

1. **Onboard a new codebase** — `OnboardProject` returns full architecture in 1 call
2. **Debug a failing endpoint** — `GetDataFlow("/api/X")` traces handler → service → DB
3. **Pre-merge impact analysis** — `GetImpactAnalysis(method, depth: 3)`
4. **Audit test coverage** — 8 frameworks supported (xUnit, NUnit, pytest, Jest, JUnit, Go test, cargo, PHPUnit)
5. **Find dead code + circular deps** — replaces NDepend / SonarQube for the common case
6. **API governance** — review middleware pipeline + endpoints before release

*Speaker: "Pick any two of these. If you do them even once a month, the productivity win pays for the setup on day one."*

---

## Slide 6 — vs the alternatives

| | Copilot | Cursor | Sourcegraph | **CortexPlexus** |
|---|:---:|:---:|:---:|:---:|
| Open source | — | — | Partial | **MIT** |
| Self-hosted | — | — | Enterprise | **Free** |
| Roslyn-deep C# | — | — | — | **Yes** |
| Knowledge Graph | — | — | Yes | **Yes** |
| MCP-native | — | Partial | — | **30 tools** |
| Source code stays local | — | — | Depends | **Always** |
| Cost / 20 devs / yr | ~$5K | ~$5K | $10K+ | **$0** |

*Speaker: "We're the only option in this matrix that's both open-source and MCP-native out of the box. If your team is on Claude Code or Cursor, this slots in with one config file."*

---

## Slide 7 — Stack credibility

- **.NET 10** monolith, MIT-licensed
- **PostgreSQL 17** — unified store (AGE graph + pgvector HNSW + tsvector BM25)
- **Roslyn** for deep C# semantics
- **Tree-sitter** for 7 more languages (TS, JS, Python, Java, Go, Rust, PHP)
- **693 tests** passing, ~85% coverage
- **2 containers**, < 2 GB RAM, < 2 GB disk
- **Zero external SaaS** dependencies (Ollama offline default; Gemini free tier optional)

*Speaker: "This isn't a demo. We have 693 tests, battle-tested benchmarks, an active release cadence — R18 landed the 556× vector speedup, R25 just closed our latest triage round."*

---

## Slide 8 — Call to action

**Get started in 3 commands:**

```bash
git clone https://github.com/DT-Tuan/CortexPlexus.git
cd cortexplexus
docker compose up -d
```

Then drop `.mcp.json` in your project root, restart your IDE, and your agent has 30 tools.

**Links**
- Repo: https://github.com/DT-Tuan/CortexPlexus
- Full feature doc: [docs/INTRODUCTION.md](./INTRODUCTION.md)
- MCP guide: [docs/MCP-GUIDE.md](./MCP-GUIDE.md)

*Speaker: "You can have this running on your laptop in 5 minutes. It's MIT. Fork it, use it, sell a service on it — we don't care. We want the platform to exist."*

---

## Appendix — talking-track cheat sheet (not a slide)

**If asked about embedding cost**: Default is Ollama offline — zero cost, zero external call. Gemini free tier is optional for ~3× faster indexing.

**If asked about privacy**: Local Agent parses source on the dev machine; only metadata (FQNs, embeddings, relationships) is uploaded. Source code never leaves the network if self-hosted.

**If asked about enterprise**: 2-container Docker, no k8s required. Scales to ~10-20K symbols per repo per indexing cycle. We've dogfooded on our own ~12K-symbol .NET repo.

**If asked about competitors (push-back)**: CortexPlexus isn't replacing Copilot's inline suggestion — it's giving any MCP-native agent structured context. They're complementary. Pair them.
