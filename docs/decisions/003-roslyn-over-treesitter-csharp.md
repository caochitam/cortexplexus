# ADR-003: Roslyn for C# Instead of Tree-sitter

**Status:** Accepted
**Date:** 2026-04-03

## Context
Mọi code knowledge graph tool hiện có (Grapuco, code-graph-mcp, codegraph, atcode...) đều dùng Tree-sitter cho tất cả ngôn ngữ, bao gồm C#. Tree-sitter cho syntax-level AST nhưng thiếu semantic analysis.

Với C#, Roslyn cung cấp full compiler-level semantic model: type resolution, overload resolution, symbol binding, flow analysis. Điều này cho phép phân tích sâu mà Tree-sitter không thể: EF Core mapping, DI container, WPF binding.

## Decision
Sử dụng **Microsoft.CodeAnalysis (Roslyn)** làm parser chính cho C#/VB.NET:
- `MSBuildWorkspace` để mở .sln/.csproj
- `Compilation.GetSemanticModel()` cho type resolution
- `CSharpSyntaxWalker` subclasses cho extraction
- `SymbolFinder` cho cross-file reference resolution

Tree-sitter vẫn dùng cho ngôn ngữ khác (TypeScript, Python, SQL) ở Phase 4.

## Consequences
- **Pro:** Deep semantic understanding — hiểu generic types, overloads, DI resolution, EF Core queries
- **Pro:** Unique differentiator — không tool nào khác có Roslyn-level C# analysis
- **Pro:** EF Core, DI, ASP.NET route analysis chỉ khả thi với Roslyn
- **Pro:** Reference implementation có sẵn: scip-dotnet (Sourcegraph)
- **Con:** Cần MSBuild installed để mở solution — heavier dependency
- **Con:** Build time lâu hơn Tree-sitter (Roslyn cần compile full project)
- **Con:** Chỉ hỗ trợ C#/VB.NET — cần Tree-sitter fallback cho ngôn ngữ khác
- **Con:** Partial classes, conditional compilation có thể phức tạp
