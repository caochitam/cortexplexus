# Security Policy

## Supported versions

CortexPlexus is in active development. Security fixes are applied to the latest release on the `main` branch. Older releases are not patched — please upgrade to the latest version before reporting an issue.

## Reporting a vulnerability

**Please do NOT open a public GitHub issue for security vulnerabilities.**

Instead, report privately via one of:

1. **GitHub Security Advisories** — recommended.
   Open a draft advisory at the [Security tab](../../security/advisories/new) of this repository.
2. **Email** — contact the maintainers (see [`CODEOWNERS`](.github/CODEOWNERS) once published).

When reporting, please include:
- A description of the vulnerability and the impact
- Steps to reproduce, ideally with a minimal proof-of-concept
- Affected versions / commit hash
- Any mitigations you have already identified
- Whether you wish to be credited in the advisory

We aim to acknowledge reports within **3 business days** and ship a fix or mitigation within **30 days** for critical issues.

## Threat model

CortexPlexus is designed for **self-hosted, single-tenant** deployments. Common assumptions:

- The MCP server endpoint (`http://localhost:8080/mcp` by default) is **not exposed to the public internet** and is reachable only by trusted IDE clients on the same machine, LAN, or VPN.
- The PostgreSQL database container is **not exposed**; only the App container talks to it.
- AI clients connecting to the server are trusted with read access to all indexed code metadata.
- The local indexing agent only sends **metadata** (FQNs, signatures, file paths, line numbers, AI summaries) to the server — never raw source code.

If you deploy CortexPlexus in a multi-tenant or public-internet scenario, **this is currently outside the supported threat model.** We welcome contributions adding authentication, rate limiting, and tenant isolation.

## What we consider in scope

- **Cypher injection** through MCP tool parameters — please report any input that escapes the `EscapeCypher` helper.
- **SQL injection** through any relational query — report any string-formatted SQL that bypasses parameterized queries.
- **Path traversal** through agent uploads, MCP tool parameters, or REST API endpoints (`/api/index/*`, `/api/agent/*`).
- **Secrets leakage** — if a parser or analyzer ingests source code containing API keys / passwords and stores them in the graph or embeddings, that is a bug. The `ISecretsScanner` is supposed to redact these before storage.
- **Denial-of-service** via expensive queries — e.g. unbounded depth on `get_callers`/`get_dependencies`, or graph patterns that cause exponential traversal.
- **Authorization bypass** — once auth is added (currently the server is unauthenticated by design for single-tenant use), bypasses become in scope.

## What we consider out of scope

- **Lack of authentication** in the default deployment. The `localhost`-only design is intentional. If you bind the server to `0.0.0.0` and put it on the public internet without a reverse proxy enforcing auth, that is a deployment misconfiguration, not a CortexPlexus vulnerability.
- **Vulnerabilities in upstream packages** (PostgreSQL, Apache AGE, pgvector, .NET runtime, ModelContextProtocol SDK). Please report those upstream. We will pin/update affected versions when fixes are available.
- **Issues in code that CortexPlexus indexes.** CortexPlexus parses source — vulnerabilities in the *parsed* code are not CortexPlexus vulnerabilities.

## Disclosure policy

- Once a fix is shipped, we will publish a GitHub Security Advisory describing the issue, affected versions, and mitigation steps.
- Reporters who want credit will be acknowledged in the advisory and the release notes.
- We follow **coordinated disclosure**: please give us reasonable time (typically 30 days for critical issues, 90 days for low-severity) to ship a fix before public disclosure.

## Hardening recommendations

If you run CortexPlexus in a sensitive environment, we recommend:

- Run behind a reverse proxy (Nginx, Caddy, Traefik) with TLS termination and basic auth.
- Restrict the MCP endpoint to known client IPs at the firewall level.
- Run the App container as non-root (the `aspnet:10.0-noble-chiseled` base image already does this).
- Mount the workspace directory **read-only** in the indexer container.
- Rotate the `GEMINI_API_KEY` if you use the Gemini provider; the key is sent only to Google's API endpoints, never logged.
- Periodically audit the `code_symbols` table for accidental secrets — the `ISecretsScanner` is best-effort and may miss novel patterns.
