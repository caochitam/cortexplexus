# CortexPlexus — Biến mã nguồn thành Knowledge Graph cho AI

**Nền tảng Code Intelligence mã nguồn mở, 100% self-hosted, miễn phí.**

AI coding assistants (Claude, Cursor, Copilot) đọc codebase như văn bản thuần túy — chúng **không hiểu cấu trúc** của code. Kết quả: agent phải `grep` rồi `read` hàng chục file để suy ra quan hệ class/method, tốn token, trả lời sai, miss context.

**CortexPlexus sửa điều đó.** Parse code bằng Roslyn (C# deep semantic) + Tree-sitter (TypeScript, JavaScript, Python, Java, Go, Rust, PHP), dựng Knowledge Graph (class, method, call graph, DI wiring, API routes, EF Core mapping, test coverage, config usage…), phục vụ cho AI agent qua **Model Context Protocol** — **1 tool call thay cho 10+ lệnh grep/read**.

---

## Giá trị cốt lõi — đo được, không tiếp thị suông

| Trước CortexPlexus | Với CortexPlexus | Lợi ích |
|---|---|---|
| Agent grep tên class → đọc 5-10 file → ghép thủ công | `GetCallers("Method.FQN")` | **1 call thay 10+ call** |
| Hiểu 1 service: đọc class + tests + dependencies (15+ file) | `ExploreTopic("ServiceName")` | **1 call thay 15+ call** |
| Trace 1 API request: Program.cs → handler → downstream | `GetDataFlow("/api/orders")` | **1 call thay 8+ call** |
| Đánh giá impact refactor: grep → đọc callers → đọc caller-của-caller | `GetImpactAnalysis(method, depth: 3)` | **1 call thay 10+ call** |
| Onboard dự án mới: đọc 20+ file thủ công | `OnboardProject(repo)` | **1 call thay 20+ call** |

**Hệ quả thực tế với AI agent**:
- Giảm 80-90% token/chi phí inference
- Agent trả lời chính xác vì có structured context, không phải đoán từ snippet
- Onboard dự án lạ trong dưới 30 giây

---

## 26 công cụ MCP — phủ đủ 4 nhóm nhu cầu

**Tìm kiếm & điều hướng** — `search_code` (hybrid full-text + vector), `semantic_search` (ngôn ngữ tự nhiên), `get_callers` / `get_callees`, `get_implementations` (interface → class), `get_class_hierarchy` (có lọc directional, không leak sibling), `get_dependencies`, `get_impact_analysis`.

**.NET deep analysis** — `get_di_registrations` (service → implementation), `get_entity_mapping` (DbContext → entity), `get_api_endpoints` (với expansion `[controller]` token), `get_data_flow` (endpoint → handler → DB), `get_middleware_pipeline` (thứ tự thực thi ASP.NET), `get_nuget_audit`, `get_architecture`.

**Chất lượng & observability** — `get_test_coverage` (hỗ trợ 8 framework: xUnit, NUnit, pytest, Jest, JUnit, Go test, Rust cargo, PHPUnit), `get_config_usage` (tìm code đọc `appsettings.json`/`.env`/`IConfiguration`/`IOptions<T>`/env-var API), `get_dead_code` (loại trừ HTTP endpoint, event subscriber, test method), `get_circular_dependencies` (DFS trên `DependsOn` graph).

**Composite** — `explore_topic` (search + callers + deps + implementations, gộp 1 call), `onboard_project` (tổng quan toàn dự án, 1 call).

---

## Hiệu năng đã đo trên dự án thật

**R18 — HNSW bulk-load (đo trên pgvector pg17):**
> Vector index phase: **51 phút → 5.5 giây (~556× nhanh hơn)** cho batch ≥500 symbols.
> Chiến lược: drop HNSW → INSERT hàng loạt → rebuild HNSW, thay vì duy trì index live.

**Scale test (CortexFlow — hệ full-stack .NET):**
> 11,633 symbols / 97,117 relationships / 8,399 embeddings indexed trong ~30 phút.
> Bottleneck là Ollama single-thread (25-30s/batch); với Gemini API free tier, thời gian giảm 3-4×.

**Search quality (hybrid fusion):**
> Apache AGE Cypher (graph) + pgvector HNSW (vector, ef_search=100 cho ~99% recall) + tsvector BM25 (full-text), gộp bằng Reciprocal Rank Fusion.

**Incremental indexing:**
> SHA-256 content hash + file watcher → re-index chỉ file thay đổi. Vòng lặp code → re-index 1 file dưới 1 giây.

---

## 6 ngữ cảnh ứng dụng điển hình

1. **Onboard dự án mới** — agent vừa kết nối đã có bản đồ kiến trúc, không cần "học" bằng cách đọc README + lang thang file.
2. **Debug bug phức tạp** — `get_data_flow("/api/failing-endpoint")` trả về chuỗi handler → service → repository → DB, xác định được pha nào breakpoint.
3. **Đánh giá impact trước merge** — `get_impact_analysis(method, depth:3)` chỉ ra chính xác bao nhiêu callers sẽ break nếu đổi signature.
4. **Audit test coverage** — tìm test phủ cho method bất kỳ, cảnh báo hot-path không có test trong CI.
5. **Clean up codebase** — `get_dead_code` + `get_circular_dependencies` phát hiện vùng loại khỏi được trong 1 call; thay vì chạy tool riêng (NDepend, SonarQube) đắt đỏ.
6. **API governance** — `get_api_endpoints` + `get_middleware_pipeline` để review chính sách security trước release.

---

## Tại sao tin tưởng được

| Tiêu chí | CortexPlexus |
|---|---|
| **Tests** | 693 test đạt (unit + integration + performance), coverage ~85% |
| **Ngôn ngữ** | 8 (C# deep với Roslyn, 7 khác với Tree-sitter) |
| **Deployment** | 2 container Docker, < 2 GB RAM, < 2 GB disk |
| **License** | MIT (thương mại hóa tự do) |
| **Dependency ngoài** | Zero (Ollama offline là default; Gemini API free tier là optional) |
| **Kiến trúc DB** | 1 PostgreSQL 17 + AGE 1.6 + pgvector 0.8.2 + tsvector — không cần Redis, RabbitMQ, Elasticsearch |

---

## So sánh nhanh với alternatives

| | GitHub Copilot | Cursor search | Sourcegraph | **CortexPlexus** |
|---|:---:|:---:|:---:|:---:|
| Mã nguồn mở | Không | Không | 1 phần | **Có (MIT)** |
| Self-hosted | Không | Không | Có (Enterprise) | **Có (miễn phí)** |
| Roslyn deep C# | Không | Không | Không | **Có** |
| Knowledge Graph (không phải text) | Không | Không | Có | **Có** |
| MCP native | Không | 1 phần | Không | **Có (30 tool)** |
| Chi phí năm / 20 dev | ~$5,000 | ~$5,000 | $10,000+ | **$0** |

---

## Bắt đầu trong 3 lệnh

```bash
git clone https://github.com/DT-Tuan/CortexPlexus.git
cd cortexplexus
docker compose up -d
```

Thêm `.mcp.json` ở gốc dự án của bạn, trỏ `http://localhost:8080/mcp`, restart IDE, và agent của bạn đã có 30 tool code intelligence. Toàn bộ source code vẫn ở máy bạn — Local Agent chỉ upload metadata.

---

## Dành cho ai

- **Team dev C# / .NET** muốn AI agent hiểu codebase enterprise (DI container, EF Core, middleware stack) mà không trả phí SaaS hàng năm.
- **Tech lead** cần audit impact / dead code / test coverage trong CI, không muốn mua công cụ $10K/năm.
- **Dev solo hoặc startup** muốn AI agent chất lượng Copilot Enterprise trên máy mình, offline, privacy-first.
- **Researcher / OSS contributor** cần nền tảng để thí nghiệm RAG-on-code, Knowledge Graph, hybrid search.

---

**Repo**: https://github.com/DT-Tuan/CortexPlexus · **License**: MIT · **Stack**: .NET 10 + PostgreSQL 17 (AGE + pgvector + tsvector) + Roslyn + Tree-sitter + Ollama/Gemini embedding + ModelContextProtocol SDK.
