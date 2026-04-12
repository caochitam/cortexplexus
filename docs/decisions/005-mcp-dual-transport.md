# ADR-005: MCP Dual Transport (stdio + HTTP)

**Status:** Accepted
**Date:** 2026-04-03

## Context
MCP SDK v1.0 hỗ trợ 2 transport modes:
- **stdio:** Process-level communication qua stdin/stdout. Zero config cho IDE local
- **HTTP/SSE:** Network transport. Cần port binding, phù hợp remote access

Hầu hết IDE (VS Code, Cursor, Claude Code) hỗ trợ cả hai. stdio đơn giản hơn cho local use, HTTP cần thiết khi chạy trong Docker.

## Decision
Hỗ trợ **cả hai transport modes:**
- `cortexplexus serve` → stdio (default, zero-config cho IDE)
- `cortexplexus serve --http` → HTTP/SSE trên port 8080
- Docker container mặc định dùng HTTP (vì stdio không khả thi cross-container)

## Consequences
- **Pro:** stdio = zero-config cho developer chạy native
- **Pro:** HTTP = cần thiết cho Docker deployment và team sharing
- **Pro:** Linh hoạt — user chọn mode phù hợp
- **Con:** Cần maintain 2 transport configurations
- **Con:** HTTP cần consider basic security (API key) khi expose ra ngoài localhost
