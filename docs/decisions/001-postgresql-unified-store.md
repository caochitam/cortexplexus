# ADR-001: PostgreSQL Unified Store (AGE + pgvector + tsvector)

**Status:** Accepted
**Date:** 2026-04-03

## Context
CortexPlexus cần 3 loại search: graph traversal, vector similarity, và full-text. Các giải pháp ban đầu xem xét:
- Neo4j Enterprise (graph) + Milvus/Pinecone (vector) + Elasticsearch (FTS) — 3 databases
- Neo4j Community + separate vector DB — 2 databases, Neo4j Community thiếu vector
- PostgreSQL + extensions — 1 database

Project target là self-hosted, free, minimal infra (2 Docker containers max).

## Decision
Sử dụng **PostgreSQL 17+ làm unified store** với 3 extensions:
- **Apache AGE** (Apache 2.0) — Cypher graph queries
- **pgvector** (PostgreSQL License) — HNSW vector similarity search
- **tsvector** (built-in) — BM25-equivalent full-text search

Graph data lưu trong AGE graph (`code_graph`). Vector + FTS data lưu trong companion table `code_symbols` với pgvector embedding column và auto-generated tsvector column.

## Consequences
- **Pro:** 1 database duy nhất → 1 Docker container, đơn giản deploy
- **Pro:** 100% free, không license cost
- **Pro:** PostgreSQL ecosystem mature, cộng đồng lớn
- **Pro:** Có thể JOIN graph results với vector/FTS results trong cùng transaction
- **Con:** Apache AGE ít mature hơn Neo4j, Cypher support chưa đầy đủ 100%
- **Con:** pgvector HNSW performance thấp hơn dedicated vector DB ở scale lớn (không ảnh hưởng ở scale self-host)
- **Con:** Cần maintain 2 copies of data (AGE graph + code_symbols table) — phải đảm bảo sync
