# CortexPlexus Agent — Template cho Project CLAUDE.md

> Copy section bên dưới vào file `CLAUDE.md` (hoặc `.cursorrules`, `.github/copilot-instructions.md`)
> ở thư mục gốc của bất kỳ project nào. AI Agent sẽ tự đọc và thực hiện.

---

## 👇 COPY TỪ ĐÂY 👇

```markdown
## CortexPlexus — Code Intelligence (Auto-Setup)

> **AI Agent: Thực hiện các bước sau KHI BẮT ĐẦU SESSION để kích hoạt code intelligence.**
> Source code KHÔNG rời khỏi máy dev — chỉ metadata (tên, signature, relationships) được gửi lên server.

### Bước 1: Kiểm tra CortexPlexus MCP đã kết nối chưa
Thử gọi `ListRepositories()`. Nếu tool không tồn tại → MCP chưa kết nối, cần tạo config file:

**Claude Code** → tạo `.mcp.json` tại project root:
```json
{
  "mcpServers": {
    "cortexplexus": {
      "type": "http",
      "url": "http://localhost:8080/mcp"
    }
  }
}
```
Sau đó restart session.

### Bước 2: Kích hoạt Local Agent
```
ActivateAgent(projectPath: "<đường dẫn tuyệt đối đến thư mục project này>")
```
Output của tool là một recipe 8 bước. Thực hiện ĐÚNG các lệnh Bash trong đó:
1. Verify prereqs (dotnet SDK) + server reachable
2. Version check (install / update if needed)
3. One-time index rồi start watch mode
4. Verify bằng `ListRepositories()` — tìm `Health: OK`
5. **Step 8 (nếu user dùng VS Code)**: tool trả về snippet `.vscode/tasks.json` pre-filled. Tạo hoặc merge vào workspace để agent tự start lần sau khi mở folder — fix vĩnh viễn cho "quên start watch → index stale".

### Bước 3: Hiểu staleness warnings
Khi gọi `ListRepositories()` hoặc search tools, xem các nhãn sau:
- Không có nhãn → index fresh, trust results.
- `(N hours ago)` không kèm STALE → OK, chỉ là informational.
- `⚠️ STALE` (> 1 ngày) → cảnh báo user, đề nghị re-run `ActivateAgent()` nếu họ vừa thay đổi code.
- `🚨 VERY STALE` (> 7 ngày) → đừng claim kết quả authoritative. Khuyến cáo re-index trước khi act.
- Search tools cũng tự append STALE footer vào output khi repo cũ — paraphrase cho user, đừng giả vờ không thấy.

### Bước 4: Sử dụng 30 tools
Sau khi agent chạy, mọi thay đổi file sẽ được tự động re-index. Dùng:
- `ExploreTopic("ClassName")` → Hiểu sâu 1 class/service (composite tool — 1 call thay cho 5+ tools)
- `OnboardProject("project-name")` → Overview toàn bộ project
- `SemanticSearch("mô tả logic cần tìm")` → Tìm code theo nghĩa
- `GetImpactAnalysis(fqn, depth:3)` → blast radius trước khi refactor
- `GetHelp("tools")` → Xem đầy đủ 30 tools. `GetHelp("memory")` cho memory playbook, `GetHelp("strategies")` cho workflow patterns.

### Bước 5: Memory (opt-in)
Kiểm tra dòng `Memory: enabled (N items)` trong output của `ListRepositories()`.
- **Nếu `disabled`** → các memory tool sẽ trả error. Skip hoặc báo user cách enable (`Memory__Enabled=true` trên server).
- **Nếu `enabled`** → đầu session gọi `RecallMemory("<topic đang làm>", scope:"project", repository:"<tên repo>", limit:5)` để thấy context prior sessions đã lưu. Đọc trước khi chạy search/explore — có thể tiết kiệm thời gian khám phá lại.
- Khi user nêu preference hoặc bạn phát hiện convention không-hiển-nhiên → `SaveMemory(content:"...", scope:"project", repository:"<tên>", topic:"preference"|"pattern"|"bug"|"decision"|"todo"|"note", importance:0.5)`.
- **Dùng `repository` name, đừng đi tìm UUID** — server resolve giúp (v0.8.3+).
- Đừng lưu cái code đã nói (dùng search tools), đừng lưu cái ADR/docs/ đã có, đừng lưu secrets.
```

## 👆 COPY ĐẾN ĐÂY 👆

---

## Biến thể cho các AI Client khác

### Cursor → thêm vào `.cursorrules`
Nội dung giống nhau, thay config file thành `.cursor/mcp.json`:
```json
{
  "mcpServers": {
    "cortexplexus": {
      "url": "http://localhost:8080/mcp"
    }
  }
}
```

### VS Code (Copilot) → thêm vào `.github/copilot-instructions.md`
Config file: `.vscode/mcp.json` (lưu ý key là `"servers"`, không phải `"mcpServers"`):
```json
{
  "servers": {
    "cortexplexus": {
      "type": "http",
      "url": "http://localhost:8080/mcp"
    }
  }
}
```

### Windsurf → thêm vào project rules
Config file: `~/.codeium/windsurf/mcp_config.json` (dùng stdio bridge):
```json
{
  "mcpServers": {
    "cortexplexus": {
      "command": "npx",
      "args": ["-y", "mcp-remote", "http://localhost:8080/mcp"]
    }
  }
}
```
