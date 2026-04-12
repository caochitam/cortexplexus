# Runbook: Development Setup

## Prerequisites
- .NET 10 SDK
- Docker Desktop (hoặc Docker Engine + Docker Compose)
- Git
- IDE: VS Code, Visual Studio, hoặc Rider
- Google Gemini API Key (free): https://aistudio.google.com/apikey

## Steps

### 1. Clone repository
```bash
git clone https://github.com/user/cortexplexus.git
cd cortexplexus
```

### 2. Setup environment
```bash
cp .env.example .env
# Edit .env: set GEMINI_API_KEY
```

### 3. Start PostgreSQL (Docker)
```bash
docker compose up postgres -d
# Wait for healthy status
docker compose ps
```

### 4. Restore & Build
```bash
dotnet restore CortexPlexus.sln
dotnet build CortexPlexus.sln
```

### 5. Run database migrations
```bash
# Applied automatically on first run, or manually:
dotnet run --project src/CortexPlexus.App -- init
```

### 6. Index a test project
```bash
dotnet run --project src/CortexPlexus.App -- index /path/to/your/csharp/project
```

### 7. Start MCP server
```bash
# stdio mode (for IDE)
dotnet run --project src/CortexPlexus.App -- serve

# HTTP mode (for Docker/remote)
dotnet run --project src/CortexPlexus.App -- serve --http

# With file watcher
dotnet run --project src/CortexPlexus.App -- serve --watch
```

### 8. Connect IDE
**Claude Code:**
```bash
claude mcp add cortexplexus --transport http http://localhost:8080/mcp
```

**VS Code (settings.json):**
```json
{
  "mcpServers": {
    "cortexplexus": {
      "command": "dotnet",
      "args": ["run", "--project", "/path/to/cortexplexus/src/CortexPlexus.App", "--", "serve"]
    }
  }
}
```

### 9. Run tests
```bash
dotnet test CortexPlexus.sln
```

## Troubleshooting

| Lỗi | Nguyên nhân | Fix |
|-----|-------------|-----|
| `FATAL: role "postgres" does not exist` | PostgreSQL chưa start | `docker compose up postgres -d` |
| `Extension "age" is not available` | Docker image sai | Đảm bảo dùng `apache/age:latest` |
| `Gemini API 429 Too Many Requests` | Vượt free tier limit | Đợi reset (daily) hoặc chuyển sang Ollama |
| `MSBuild not found` | Thiếu .NET SDK workload | `dotnet workload install msbuild` |
| `Cannot open solution` | Roslyn cần MSBuild | Cài Visual Studio Build Tools hoặc .NET SDK |
