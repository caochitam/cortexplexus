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
Thực hiện đúng các lệnh mà tool trả về:
1. Kiểm tra binary → cài nếu chưa có
2. Start watch mode (background)
3. Verify bằng `ListRepositories()`

### Bước 3: Sử dụng
Sau khi agent chạy, mọi thay đổi file sẽ được tự động re-index. Dùng:
- `ExploreTopic("ClassName")` → Hiểu sâu 1 class/service
- `OnboardProject("project-name")` → Overview toàn bộ project
- `SemanticSearch("mô tả logic cần tìm")` → Tìm code theo nghĩa
- `GetHelp()` → Xem đầy đủ 21 tools
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
