# ADR-004: Google Gemini Embedding (Free Tier) as Default

**Status:** Accepted
**Date:** 2026-04-03

## Context
CortexPlexus cần embedding model cho semantic code search. Các options:
- Azure OpenAI (text-embedding-3-large): Trả phí, 3072-dim, chất lượng cao
- Ollama + nomic-embed-text: Free, local, 768-dim, không cần internet
- Google Gemini (gemini-embedding-001): Free tier (10K req/ngày), 3072-dim (reducible), code-native

Project cam kết $0 chi phí vận hành. Cần model chất lượng cao + free.

## Decision
Sử dụng **Google Gemini Embedding (`gemini-embedding-001`)** làm default provider:
- Free tier: 1,500 RPM, 1M tokens/phút, 10,000 requests/ngày
- Dimensions: 3072 (giảm xuống 768 via `outputDimensionality` để tiết kiệm storage)
- Code-native: hỗ trợ `taskType: "CODE_RETRIEVAL_QUERY"`
- Integration: HTTP API + API key (tạo free tại Google AI Studio)

**Ollama** làm offline fallback khi không có internet.

**Embedding strategy:** Chỉ gửi `method signature + summary` — KHÔNG gửi full source code.

## Consequences
- **Pro:** $0 chi phí — free tier đủ cho self-host cá nhân/team nhỏ
- **Pro:** Chất lượng cao (3072-dim, Gemini architecture)
- **Pro:** Code-specific task type support
- **Pro:** Ollama fallback đảm bảo offline capability
- **Con:** Phụ thuộc Google API — nếu free tier thay đổi, cần chuyển sang Ollama
- **Con:** Cần internet cho default mode (Ollama giải quyết vấn đề này)
- **Con:** Method signatures gửi đến Google — acceptable vì không chứa business logic chi tiết
