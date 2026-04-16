# REST API reference

CortexPlexus exposes a small REST surface alongside the MCP transport. This page is the schema spec for the endpoints the Local Agent + IDE plugins actually depend on. Other endpoints exist (graph explorer JSON, etc.) but are unstable — call those at your own risk.

Conventions:

- All paths are relative to the server base URL (default `http://localhost:8080`, override per [`MCP-GUIDE.md` § 2](MCP-GUIDE.md)).
- All bodies are `application/json` with `camelCase` keys.
- Times are ISO 8601 UTC. Sizes are bytes. SHA256s are uppercase hex.
- "Required" means the server returns 400 if missing; "optional" means default-or-absent.
- **Wire-compat policy**: when a field is renamed, the old name is kept as an alias for **one minor release**. The CHANGELOG flags both the rename (under `Changed`) and the eventual alias drop (under `Deprecated` then `Removed`).

---

## `GET /api/agent/version`

Reports the agent version and per-platform tarball SHA256s. Used by the Local Agent's update check (`AgentUpdater`) and by the `ActivateAgent` MCP tool's Step 3.

**Response 200**
```json
{
  "version": "1.1.0",
  "platforms": ["win-x64", "linux-x64", "osx-x64"],
  "sha256": {
    "win-x64":  "A73B016DCEEBFB8FDBA01DC2FC4A87F551EA16BCFCC9ADBA93820B8F48F20300",
    "linux-x64":"5B572D9F6A3301D4A9B6DB0E4991F19BC16B2715EF33F47F3482233DFAA25B64",
    "osx-x64":  "2520FA089E159B7D9215E53768CEF7234BA0E3849539D1BCC6DB21538EF2DBEF"
  }
}
```

| Field | Type | Notes |
|---|---|---|
| `version` | string | Semver from `CortexPlexus.Core.AgentInfo.Version`. SSOT for both server + agent CLI. |
| `platforms` | string[] | Allow-list of valid `platform` query values for `/api/agent/download`. |
| `sha256` | object<string,string> | Map of `<rid>` → uppercase SHA256 of the tarball that `/api/agent/download?platform=<rid>` will serve. Computed at request time from whichever location resolves (image-bundled `/app/_agent/` first, then `${Workspace__Path}/_agent/`). |

---

## `GET /api/agent/download?platform=<rid>`

Streams the agent tarball for the requested platform. Resolves image-bundled location first (shipped with the Docker image), then `${Workspace__Path}/_agent/` as an operator override.

**Query**
| Param | Type | Required | Notes |
|---|---|---|---|
| `platform` | enum: `win-x64` \| `linux-x64` \| `osx-x64` | optional | Defaults to the server's own RID; usually you want to specify. |

**Response 200**: `application/gzip` byte stream, `Content-Disposition: attachment; filename="cortexplexus-agent-<rid>.tar.gz"`. Verify against `sha256[rid]` from `/api/agent/version`.

**Response 400**: `{ "error": "Invalid platform '<rid>'..." }`

**Response 404**: `{ "error": "Agent not found for platform '<rid>'..." }`

---

## `POST /api/index/results`

The agent uploads parsed metadata for one chunk of a project. Always returns 200 even on partial persist failure — **read the response fields below to detect that case**, do not assume HTTP 200 means the chunk landed cleanly.

**Request body**
```json
{
  "projectName": "MyProject",
  "symbols": [ /* SymbolDto[] */ ],
  "relationships": [ /* RelationshipDto[] */ ],
  "fileHashes": { "src/Foo.cs": "A1B2..." }
}
```

| Field | Type | Required | Notes |
|---|---|---|---|
| `projectName` | string | yes | Used as the repo's display `name`. Repo path is `_agent/<projectName>`. |
| `symbols` | SymbolDto[] | one of these three must be non-empty | Per-symbol metadata. See [`src/CortexPlexus.App/Api/Dto/IndexResultsDto.cs`](../src/CortexPlexus.App/Api/Dto/IndexResultsDto.cs) for the shape. Source code is NOT included. |
| `relationships` | RelationshipDto[] | (same) | Edges between symbols. |
| `fileHashes` | object<string,string> | (same) | SHA256 per file path. The presence of this field on the FINAL chunk marks the indexing run "complete" (server updates `last_indexed`). |

**Response 200**
```json
{
  "project": "MyProject",
  "symbols": 2000,
  "relationships": 0,
  "embeddings": 479,
  "symbolsPersisted": 1860,
  "symbolsFailed": 0,
  "vectorRowsWritten": 479,
  "warnings": [],
  "durationSeconds": 110.7,

  "embeddingsPersisted": 1860,
  "embeddingsFailed": 0
}
```

| Field | Type | Notes |
|---|---|---|
| `project` | string | Echo of `projectName`. |
| `symbols` | int | Count of symbols **received** in this chunk (after server-side normalization, before dedup). |
| `relationships` | int | Edges received in this chunk. |
| `embeddings` | int | Symbols that the server attempted to embed (kind ∈ `EmbeddableKinds.All`). Note: less than `symbols` because field/property/event/constructor are intentionally not embedded — see [`HEALTH-METRICS.md`](HEALTH-METRICS.md). |
| **`symbolsPersisted`** | int | **NEW in v0.7.0.** Count of symbol rows that landed in `code_symbols` (with or without an embedding). Equals `symbols` after dedup minus `symbolsFailed`. |
| **`symbolsFailed`** | int | **NEW in v0.7.0.** Count of symbol rows that the vector-store batch dropped (typically a serialization or DB-side error). Non-zero means the chunk is incomplete; the agent treats this as a hard error and aborts. |
| **`vectorRowsWritten`** | int | **NEW in v0.7.0.** Count of `code_symbols` rows where `embedding IS NOT NULL` after this chunk. Useful to distinguish "row inserted with NULL embedding" (expected for non-embeddable kinds) from "row inserted with vector". |
| `warnings` | string[] | Human-readable warnings collected during persist. Empty on success. Always present, possibly empty. |
| `durationSeconds` | double | Wall-clock time for this chunk. |
| `embeddingsPersisted` | int | **DEPRECATED v0.7.0**, removed v0.8.0. Alias of `symbolsPersisted` — the historical name was misleading because the value counts symbol rows, not embedding rows. Old (v1.1.0) agents read this; new (v1.2.0+) agents prefer `symbolsPersisted` and fall back. |
| `embeddingsFailed` | int | **DEPRECATED v0.7.0**, removed v0.8.0. Alias of `symbolsFailed`. Same reasoning. |

### Detecting partial-persist failures (agent contract)

```
if response.symbolsFailed > 0  →  treat upload as failed; abort the rest of the chunked sequence.
if response.warnings is non-empty  →  log each warning at ERROR level for the user.
if response.vectorRowsWritten == 0 and response.embeddings > 0  →  vector pipeline appears down; warn loudly even if symbolsFailed == 0 (some kinds may have skipped via SymbolsFailed=0 / VectorRowsWritten=0 path).
```

The agent ships this logic in `LocalIndexer.PostChunkAsync`. Old agents (v1.1.0) check only `embeddingsFailed`, which is preserved for one release.

---

## `GET /api/index/{projectName}/hashes`

Returns the SHA256 the server has on file for every previously indexed file in the project, so the agent can compute a delta on the next run.

**Response 200**
```json
{ "src/Foo.cs": "A1B2...", "src/Bar.cs": "C3D4..." }
```

Empty object `{}` if the project doesn't exist server-side. The agent treats that as "first index → upload everything."

---

## See also

- [`docs/HEALTH-METRICS.md`](HEALTH-METRICS.md) — `list_repositories` Health label semantics (relies on `embedding IS NOT NULL` rather than the upload response).
- [`src/CortexPlexus.App/Api/Dto/IndexResultsDto.cs`](../src/CortexPlexus.App/Api/Dto/IndexResultsDto.cs) — authoritative type definitions.
- [`docs/PLAN-v0.7.0.md`](PLAN-v0.7.0.md) Item #2 — the rationale for the rename and the wire-compat strategy.
