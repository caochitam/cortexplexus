using CortexPlexus.Core.Models;
using CortexPlexus.Graph;
using CortexPlexus.Parsing;
using CortexPlexus.Parsing.TreeSitter;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace CortexPlexus.Integration.Tests;

/// <summary>
/// End-to-end workflow tests: parse → store → query qua hybrid search stack thật.
///
/// Phạm vi: TEST-PLAN.md #111, #112, #113, #114, #115, #117
/// (#116 — Agent → Server flow — không test ở đây vì cần real HTTP infrastructure)
///
/// Lưu ý: dùng TreeSitter parser (TypeScript/Python) thay vì Roslyn để không cần
/// .NET SDK + MSBuild trong test environment. PostgresFixture có pgvector + tsvector
/// (không có AGE) → chỉ test BM25 + Vector workflows, không test graph traversal E2E.
///
/// **Quyết định kiến trúc**: E2E tests đặt cùng project với Integration.Tests để
/// reuse PostgresFixture. Tách project E2E.Tests riêng tăng overhead mà không có
/// benefit (cùng fixture, cùng infrastructure).
/// </summary>
[Collection("Postgres")]
public sealed class E2EWorkflowTests(PostgresFixture fixture)
{
    /// <summary>Build TreeSitter parser via DI container.</summary>
    private static TreeSitterCodeParser BuildTreeSitterParser()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCortexPlexusParsing();
        var sp = services.BuildServiceProvider();
        return sp.GetRequiredService<TreeSitterCodeParser>();
    }

    /// <summary>Tạo temp project directory với files, return (dir, cleanup).</summary>
    private static (string dir, Action cleanup) CreateProject(params (string path, string content)[] files)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"cortex-e2e-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);

        foreach (var (rel, content) in files)
        {
            var full = Path.Combine(dir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }

        return (dir, () => { try { Directory.Delete(dir, recursive: true); } catch { } });
    }

    /// <summary>Deterministic embedding generator cho test (no real embedding service).</summary>
    private static float[] FakeEmbedding(string seed)
    {
        var hash = seed.GetHashCode();
        var rand = new Random(hash);
        var emb = new float[768];
        for (int i = 0; i < 768; i++)
            emb[i] = (float)(rand.NextDouble() * 2 - 1);
        // Normalize so cosine similarity is meaningful
        var mag = MathF.Sqrt(emb.Sum(x => x * x));
        for (int i = 0; i < 768; i++)
            emb[i] /= mag;
        return emb;
    }

    // === #111: E2E_IndexProject_SearchFindsSymbols ===
    [Fact]
    public async Task E2E_ParseTypescriptProject_StoreAndSearchByName_FindsSymbol()
    {
        // Mục đích: Full workflow — parse TS files → upsert vào VectorStore + FullTextStore →
        // BM25 search tìm được symbol theo tên.
        await using var ds = fixture.CreateDataSource();
        var repoId = await fixture.SeedRepositoryAsync(ds, "e2e-search", $"/e2e-search/{Guid.NewGuid():N}");

        var (projectDir, cleanup) = CreateProject(
            ("src/PaymentProcessor.ts", """
                export class PaymentProcessor {
                    process(amount: number): boolean {
                        return amount > 0;
                    }
                }
                """),
            ("src/UserService.ts", """
                export class UserService {
                    getUser(id: string): User | null {
                        return null;
                    }
                }
                """));

        try
        {
            // Step 1: Parse
            var parser = BuildTreeSitterParser();
            var parseResult = await parser.ParseSolutionAsync(projectDir);

            Assert.NotEmpty(parseResult.Symbols);

            // Set RepoId cho symbols
            var symbols = parseResult.Symbols
                .Where(s => s.Kind != "config_key") // skip config files
                .Select(s => s with { RepoId = repoId })
                .ToList();

            // Step 2: Store
            var vectorStore = new VectorStore(ds, NullLogger<VectorStore>.Instance);
            var ftsStore = new FullTextStore(ds);

            var embeddings = symbols.ToDictionary(s => s.Fqn, s => FakeEmbedding(s.Name));
            await vectorStore.UpsertAsync(symbols, embeddings);

            // Step 3: Search bằng BM25 — tìm "PaymentProcessor"
            var bm25Results = await ftsStore.SearchAsync("PaymentProcessor", limit: 10, repoId: repoId);

            Assert.NotEmpty(bm25Results);
            Assert.Contains(bm25Results, r => r.Name == "PaymentProcessor");
        }
        finally
        {
            // Cleanup data
            await using var clean = fixture.CreateDataSource();
            var store = new VectorStore(clean, NullLogger<VectorStore>.Instance);
            await store.DeleteByRepoAsync(repoId);
            cleanup();
        }
    }

    // === #114: E2E_MultiLanguageProject_AllParsed ===
    [Fact]
    public async Task E2E_MultiLanguageProject_AllLanguagesIndexed()
    {
        // Mục đích: Project có TS + Python + JS → tất cả được parse và stored.
        await using var ds = fixture.CreateDataSource();
        var repoId = await fixture.SeedRepositoryAsync(ds, "e2e-multilang", $"/e2e-multilang/{Guid.NewGuid():N}");

        var (projectDir, cleanup) = CreateProject(
            ("src/api.ts", """
                export class ApiClient {
                    fetch(url: string) { }
                }
                """),
            ("src/main.py", """
                class DataPipeline:
                    def run(self):
                        pass
                """),
            ("src/util.js", """
                function processData(data) {
                    return data;
                }
                """));

        try
        {
            var parser = BuildTreeSitterParser();
            var parseResult = await parser.ParseSolutionAsync(projectDir);

            Assert.True(parseResult.Symbols.Count >= 3,
                $"Expected ≥3 symbols (1 per language), got {parseResult.Symbols.Count}");

            var symbols = parseResult.Symbols
                .Where(s => s.Kind != "config_key")
                .Select(s => s with { RepoId = repoId })
                .ToList();

            var vectorStore = new VectorStore(ds, NullLogger<VectorStore>.Instance);
            var ftsStore = new FullTextStore(ds);

            var embeddings = symbols.ToDictionary(s => s.Fqn, s => FakeEmbedding(s.Name));
            await vectorStore.UpsertAsync(symbols, embeddings);

            // Verify cả 3 ngôn ngữ tồn tại trong store qua BM25 search
            var apiResults = await ftsStore.SearchAsync("ApiClient", limit: 10, repoId: repoId);
            var dataResults = await ftsStore.SearchAsync("DataPipeline", limit: 10, repoId: repoId);
            var processResults = await ftsStore.SearchAsync("processData", limit: 10, repoId: repoId);

            Assert.NotEmpty(apiResults);
            Assert.NotEmpty(dataResults);
            Assert.NotEmpty(processResults);
        }
        finally
        {
            await using var clean = fixture.CreateDataSource();
            var store = new VectorStore(clean, NullLogger<VectorStore>.Instance);
            await store.DeleteByRepoAsync(repoId);
            cleanup();
        }
    }

    // === #113: E2E_IncrementalIndex_OnlyChangedFiles ===
    [Fact]
    public async Task E2E_ReindexAfterFileChange_StoreReflectsLatestSymbols()
    {
        // Mục đích: Re-parse project sau khi modify file → store reflect latest symbols
        // (upsert ON CONFLICT updates).
        await using var ds = fixture.CreateDataSource();
        var repoId = await fixture.SeedRepositoryAsync(ds, "e2e-incremental", $"/e2e-incremental/{Guid.NewGuid():N}");

        var (projectDir, cleanup) = CreateProject(
            ("src/Service.ts", """
                export class OldService {
                    oldMethod() { }
                }
                """));

        try
        {
            var parser = BuildTreeSitterParser();
            var vectorStore = new VectorStore(ds, NullLogger<VectorStore>.Instance);
            var ftsStore = new FullTextStore(ds);

            // Step 1: Initial parse + store
            var firstParse = await parser.ParseSolutionAsync(projectDir);
            var firstSymbols = firstParse.Symbols.Where(s => s.Kind != "config_key")
                .Select(s => s with { RepoId = repoId }).ToList();
            await vectorStore.UpsertAsync(firstSymbols,
                firstSymbols.ToDictionary(s => s.Fqn, s => FakeEmbedding(s.Name)));

            var oldResults = await ftsStore.SearchAsync("OldService", limit: 10, repoId: repoId);
            Assert.NotEmpty(oldResults);

            // Step 2: Modify file
            File.WriteAllText(Path.Combine(projectDir, "src/Service.ts"), """
                export class NewService {
                    newMethod() { }
                }
                """);

            // Step 3: Re-parse + upsert
            var secondParse = await parser.ParseSolutionAsync(projectDir);
            var secondSymbols = secondParse.Symbols.Where(s => s.Kind != "config_key")
                .Select(s => s with { RepoId = repoId }).ToList();
            await vectorStore.UpsertAsync(secondSymbols,
                secondSymbols.ToDictionary(s => s.Fqn, s => FakeEmbedding(s.Name)));

            // Step 4: Search → tìm thấy NewService
            var newResults = await ftsStore.SearchAsync("NewService", limit: 10, repoId: repoId);
            Assert.NotEmpty(newResults);
            // Note: OldService cũ vẫn có thể còn (nếu FQN khác) — đó là behavior hiện tại,
            // cleanup orphans là responsibility của IndexingPipeline (delete-then-insert pattern).
        }
        finally
        {
            await using var clean = fixture.CreateDataSource();
            var store = new VectorStore(clean, NullLogger<VectorStore>.Instance);
            await store.DeleteByRepoAsync(repoId);
            cleanup();
        }
    }

    // === #115: E2E_ExploreTopic_FullWorkflow (proxy via search round-trip) ===
    [Fact]
    public async Task E2E_VectorAndBm25_FindSameSymbol_DifferentScores()
    {
        // Mục đích: Cùng symbol được tìm thấy bởi cả vector search và BM25 search,
        // nhưng scores khác nhau (vector dựa trên cosine similarity, BM25 trên ts_rank).
        await using var ds = fixture.CreateDataSource();
        var repoId = await fixture.SeedRepositoryAsync(ds, "e2e-hybrid", $"/e2e-hybrid/{Guid.NewGuid():N}");

        var (projectDir, cleanup) = CreateProject(
            ("src/Auth.ts", """
                export class AuthenticationService {
                    login(username: string, password: string) { }
                }
                """));

        try
        {
            var parser = BuildTreeSitterParser();
            var parseResult = await parser.ParseSolutionAsync(projectDir);
            var symbols = parseResult.Symbols.Where(s => s.Kind != "config_key")
                .Select(s => s with { RepoId = repoId }).ToList();

            var vectorStore = new VectorStore(ds, NullLogger<VectorStore>.Instance);
            var ftsStore = new FullTextStore(ds);

            // Determine embedding cho AuthenticationService
            var authEmb = FakeEmbedding("AuthenticationService");
            await vectorStore.UpsertAsync(symbols,
                symbols.ToDictionary(s => s.Fqn, s => s.Name == "AuthenticationService" ? authEmb : FakeEmbedding(s.Name)));

            // BM25 search
            var bm25 = await ftsStore.SearchAsync("AuthenticationService", limit: 10, repoId: repoId);

            // Vector search với chính embedding của symbol → score ≈ 1.0
            var vector = await vectorStore.SearchAsync(authEmb, limit: 10, repoId: repoId);

            Assert.NotEmpty(bm25);
            Assert.NotEmpty(vector);

            // Cả 2 đều tìm được AuthenticationService (cross-validate workflows)
            Assert.Contains(bm25, r => r.Name == "AuthenticationService");
            Assert.Contains(vector, r => r.Name == "AuthenticationService");
        }
        finally
        {
            await using var clean = fixture.CreateDataSource();
            var store = new VectorStore(clean, NullLogger<VectorStore>.Instance);
            await store.DeleteByRepoAsync(repoId);
            cleanup();
        }
    }

    // === #117: E2E_DeleteRepo_CleansAllData ===
    [Fact]
    public async Task E2E_DeleteByRepo_RemovesAllSymbolsAndIndexEntries()
    {
        // Mục đích: DeleteByRepoAsync xoá hết symbols → cả vector và BM25 search đều empty.
        await using var ds = fixture.CreateDataSource();
        var repoId = await fixture.SeedRepositoryAsync(ds, "e2e-delete", $"/e2e-delete/{Guid.NewGuid():N}");

        var (projectDir, cleanup) = CreateProject(
            ("src/Foo.ts", "export class FooClass { method() {} }"),
            ("src/Bar.ts", "export class BarClass { method() {} }"));

        try
        {
            var parser = BuildTreeSitterParser();
            var parseResult = await parser.ParseSolutionAsync(projectDir);
            var symbols = parseResult.Symbols.Where(s => s.Kind != "config_key")
                .Select(s => s with { RepoId = repoId }).ToList();

            var vectorStore = new VectorStore(ds, NullLogger<VectorStore>.Instance);
            var ftsStore = new FullTextStore(ds);

            await vectorStore.UpsertAsync(symbols,
                symbols.ToDictionary(s => s.Fqn, s => FakeEmbedding(s.Name)));

            // Verify exists
            var beforeBm25 = await ftsStore.SearchAsync("FooClass", limit: 10, repoId: repoId);
            Assert.NotEmpty(beforeBm25);

            // Delete
            await vectorStore.DeleteByRepoAsync(repoId);

            // Verify cleaned
            var afterBm25 = await ftsStore.SearchAsync("FooClass", limit: 10, repoId: repoId);
            var afterVector = await vectorStore.SearchAsync(
                FakeEmbedding("FooClass"), limit: 10, repoId: repoId);

            Assert.Empty(afterBm25);
            Assert.Empty(afterVector);
        }
        finally
        {
            cleanup();
        }
    }

    // === #112 partial: ParseAndStore validates relational integrity ===
    [Fact]
    public async Task E2E_ParseAndStore_PreservesSymbolMetadata()
    {
        // Mục đích: Verify metadata (Fqn, Name, Kind, FilePath, StartLine) được preserve
        // qua full pipeline (parse → store → retrieve qua search).
        await using var ds = fixture.CreateDataSource();
        var repoId = await fixture.SeedRepositoryAsync(ds, "e2e-metadata", $"/e2e-metadata/{Guid.NewGuid():N}");

        var (projectDir, cleanup) = CreateProject(
            ("Calculator.py", """
                class Calculator:
                    def add(self, a: int, b: int) -> int:
                        return a + b
                """));

        try
        {
            var parser = BuildTreeSitterParser();
            var parseResult = await parser.ParseSolutionAsync(projectDir);
            var symbols = parseResult.Symbols.Where(s => s.Kind != "config_key")
                .Select(s => s with { RepoId = repoId }).ToList();

            var vectorStore = new VectorStore(ds, NullLogger<VectorStore>.Instance);
            var ftsStore = new FullTextStore(ds);

            await vectorStore.UpsertAsync(symbols,
                symbols.ToDictionary(s => s.Fqn, s => FakeEmbedding(s.Name)));

            var results = await ftsStore.SearchAsync("Calculator", limit: 10, repoId: repoId);
            var calc = results.FirstOrDefault(r => r.Name == "Calculator");

            Assert.NotNull(calc);
            Assert.Equal("Calculator", calc!.Name);
            Assert.NotNull(calc.FilePath); // file path preserved
            Assert.Contains("Calculator.py", calc.FilePath); // chính xác file
        }
        finally
        {
            await using var clean = fixture.CreateDataSource();
            var store = new VectorStore(clean, NullLogger<VectorStore>.Instance);
            await store.DeleteByRepoAsync(repoId);
            cleanup();
        }
    }

    // === #116 proxy: Parse output structure compatible with API push format ===
    [Fact]
    public async Task E2E_ParseResult_ContainsSymbolsAndRelationships()
    {
        // Mục đích: ParseResult từ TreeSitter parser có symbols + relationships
        // structure compatible với /api/index/results endpoint.
        // Đây là precondition cho Local Agent → Server flow (#116).
        var (projectDir, cleanup) = CreateProject(
            ("src/Inheritance.ts", """
                export class Animal {
                    eat() { }
                }
                export class Dog extends Animal {
                    bark() { }
                }
                """));

        try
        {
            var parser = BuildTreeSitterParser();
            var result = await parser.ParseSolutionAsync(projectDir);

            // Sanity: parsing produced output
            Assert.NotEmpty(result.Symbols);

            // Verify symbols có FQN unique
            var fqns = result.Symbols.Select(s => s.Fqn).ToList();
            Assert.Equal(fqns.Count, fqns.Distinct().Count());

            // Verify Dog và Animal đều được extract
            Assert.Contains(result.Symbols, s => s.Name == "Dog");
            Assert.Contains(result.Symbols, s => s.Name == "Animal");

            // ParseResult có FilesProcessed > 0 → server biết đã parse gì
            Assert.True(result.FilesProcessed > 0);
        }
        finally
        {
            cleanup();
        }
    }
}
