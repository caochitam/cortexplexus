# Contributing to CortexPlexus

Thank you for considering contributing! CortexPlexus is built by and for developers who want to give AI coding assistants real structural understanding of source code. We welcome bug reports, feature requests, documentation fixes, and pull requests.

## Code of Conduct

Participation in this project is governed by our [Code of Conduct](CODE_OF_CONDUCT.md). By participating, you agree to uphold it. Report unacceptable behavior to the maintainers.

## How to contribute

### Reporting bugs

1. **Search existing issues** first to avoid duplicates.
2. Open a new issue using the **Bug Report** template.
3. Include:
   - CortexPlexus version (commit hash or release tag)
   - Steps to reproduce
   - Expected vs. actual behavior
   - Server logs (`docker logs cortexplexus-app --tail 100`)
   - The MCP tool call that triggered the issue, if applicable

### Suggesting features

1. Open an issue using the **Feature Request** template.
2. Describe the use case before the implementation. "I want to know which tests cover a controller" is more useful than "add a new MCP tool called `GetTestCoverage`".
3. Check [`docs/ROADMAP.md`](docs/ROADMAP.md) for what's already planned.

### Submitting pull requests

1. **Fork** the repo and create a feature branch off `main`.
2. Make focused commits with clear messages. Conventional Commits style is welcome but not required.
3. Add tests for any new behavior. We have **693 tests** across 10 projects; new code without tests is unlikely to be accepted.
4. Run the full test suite locally before pushing (see "Running tests" below).
5. Update [`docs/TESTING.md`](docs/TESTING.md) if your change affects performance characteristics.
6. Open a PR using the **Pull Request** template. Link the issue it closes.

## Development setup

### Prerequisites

- **.NET 10 SDK** ([download](https://dotnet.microsoft.com/download))
- **Docker + Docker Compose** for the PostgreSQL container
- **Apache AGE** + **pgvector** PostgreSQL extensions (provided by the `apache/age` Docker image used in tests)

### Clone + build

```bash
git clone https://github.com/DT-Tuan/cortexplexus.git
cd cortexplexus
dotnet build src/CortexPlexus.App/CortexPlexus.App.csproj
```

### Running locally with Docker

```bash
cp .env.example .env
docker compose up -d

# Verify
curl -s -o /dev/null -w "%{http_code}\n" http://localhost:8080/mcp   # 400 = OK
docker logs cortexplexus-app --tail 30
```

### Running natively (no Docker)

You need a PostgreSQL instance with `age` and `vector` extensions installed.

```bash
# Initialize schema
dotnet run --project src/CortexPlexus.App -- init

# Start the MCP server
dotnet run --project src/CortexPlexus.App -- serve --watch /path/to/your/code
```

## Running tests

The test suite is split into 10 projects. They are independent — you can run a subset:

```bash
# Unit tests (fast, no infrastructure)
dotnet test tests/CortexPlexus.Core.Tests
dotnet test tests/CortexPlexus.Parsing.Tests
dotnet test tests/CortexPlexus.App.Tests
dotnet test tests/CortexPlexus.Mcp.Tests
dotnet test tests/CortexPlexus.Embedding.Tests
dotnet test tests/CortexPlexus.Search.Tests

# Integration tests (spin up Apache AGE container via Testcontainers)
dotnet test tests/CortexPlexus.Graph.Tests
dotnet test tests/CortexPlexus.Integration.Tests
dotnet test tests/CortexPlexus.Api.Tests
dotnet test tests/CortexPlexus.Agent.Tests
```

Integration tests require Docker to be running locally. They start a `apache/age:latest` container per test fixture and clean up afterward.

## Coding conventions

- **C# 13 / .NET 10** with nullable reference types enabled
- **Records** for all domain models — immutable by default
- **`sealed`** unless inheritance is explicitly needed
- **Async by default** for I/O methods (`Task<T>`, `CancellationToken`)
- **Constructor injection** via the primary-constructor syntax (`public class Foo(IDep dep)`)
- **Interface-first design** in `CortexPlexus.Core.Abstractions` for everything that crosses module boundaries
- **No raw SQL in MCP tool handlers** — go through `IGraphStore` / `IVectorStore` / `IFullTextStore`

### Module dependency rules

```
CortexPlexus.App ──references──> ALL libraries below
CortexPlexus.Search ──references──> Core, Graph
CortexPlexus.Graph ──references──> Core
CortexPlexus.Parsing ──references──> Core
CortexPlexus.Embedding ──references──> Core
CortexPlexus.Agent ──references──> Core, Parsing  (standalone CLI)

CONSTRAINT: Core CANNOT reference any other project
CONSTRAINT: Parsing CANNOT reference Graph, Search, Embedding
CONSTRAINT: Embedding CANNOT reference Graph, Search, Parsing
CONSTRAINT: Graph CANNOT reference Parsing, Search, Embedding
CONSTRAINT: Agent CANNOT reference Graph, Search, Embedding, App
```

CI will fail if you violate these.

## Adding a new MCP tool

1. Add the handler method to one of the `*Tools.cs` files under `src/CortexPlexus.App/Mcp/Tools/` with `[McpServerTool, Description("...")]` attributes.
2. Required parameters should be **nullable with explicit validation** that returns a friendly error string — do NOT let the SDK throw `ArgumentException`. Use the `RequireFqn` helper or write your own.
3. If the tool reads from the graph, add the query method to `IGraphStore` and implement it in `AgeGraphStore`. Route through `ExecuteCypherQuery` for kind-resolution and dedup.
4. If the tool reads from the vector store, add it to `IVectorStore` and `VectorStore`.
5. Add at least one **integration test** in `tests/CortexPlexus.Graph.Tests` (with `AgeFixture`) and/or `tests/CortexPlexus.Mcp.Tests` (with mock graph store).
6. Update the tool count and description in [`README.md`](README.md) and [`src/CortexPlexus.App/Mcp/Tools/HelpTools.cs`](src/CortexPlexus.App/Mcp/Tools/HelpTools.cs).

## Adding a new language parser

The Tree-sitter pipeline lives in `src/CortexPlexus.Parsing/TreeSitter/`. To add a new language:

1. Make sure `TreeSitter.DotNet` ships a grammar for it (or vendor a custom build).
2. Create `<Language>Extractor.cs` implementing the symbol/relationship extraction.
3. Wire it into `TreeSitterCodeParser` based on file extension.
4. Add tests for symbol extraction, call graph, and config-key access patterns.
5. Update the language list in [`README.md`](README.md).

## Documentation

- **API / tool docs** live in [`docs/MCP-GUIDE.md`](docs/MCP-GUIDE.md) and `HelpTools.cs`.
- **Architecture docs** live in [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md).
- **Runbooks** (deployment, dev setup, troubleshooting) live in [`docs/runbooks/`](docs/runbooks/).
- **Decision records** live in [`docs/decisions/`](docs/decisions/).

When you fix a non-trivial bug, consider adding a "Round NN" section to [`docs/TESTING.md`](docs/TESTING.md) with root cause + verification notes.

## License

By contributing, you agree that your contributions will be licensed under the [MIT License](LICENSE).

## Questions?

Open a [discussion](https://github.com/DT-Tuan/cortexplexus/discussions) or file an issue.
