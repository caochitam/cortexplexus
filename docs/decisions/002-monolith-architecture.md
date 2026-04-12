# ADR-002: Monolith Architecture (Single .NET App)

**Status:** Accepted
**Date:** 2026-04-03

## Context
Ban đầu thiết kế với microservices: Ingestion API, Indexing Worker, MCP Server riêng biệt + RabbitMQ + Redis. Phù hợp cho enterprise 500 users nhưng quá phức tạp cho target user mới: individual developer / small team tự host.

Benchmark: Grapuco dùng CLI + cloud SaaS. codegraph, code-graph-mcp dùng single binary. Cả hai đều targeting individual devs.

## Decision
Gộp tất cả vào **1 .NET application duy nhất** (CortexPlexus.App):
- MCP Server (ASP.NET Core + MCP SDK)
- Indexing Worker (BackgroundService)
- File Watcher (FileSystemWatcher)
- CLI commands (init, index, serve, status, search)

Thay thế external infrastructure bằng in-process alternatives:
- RabbitMQ → `Channel<T>` (System.Threading.Channels)
- Redis → `IMemoryCache`
- Reverse proxy → Kestrel trực tiếp

## Consequences
- **Pro:** 1 container duy nhất (+ PostgreSQL = tổng 2) — `docker compose up` xong
- **Pro:** Zero external dependency ngoài PostgreSQL
- **Pro:** Đơn giản debug, deploy, maintain
- **Pro:** Phù hợp target user (dev/small team, không cần DevOps)
- **Con:** Không scale horizontal (đủ cho self-host, không phù hợp 500+ concurrent users)
- **Con:** Queue mất khi restart (chấp nhận — sẽ re-index)
- **Con:** Cache mất khi restart (chấp nhận — warm up nhanh)
