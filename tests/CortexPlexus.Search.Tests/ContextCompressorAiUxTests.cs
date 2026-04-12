using CortexPlexus.Core.Models;
using CortexPlexus.Search;

namespace CortexPlexus.Search.Tests;

/// <summary>
/// AI Agent UX tests cho ContextCompressor — verify token efficiency,
/// level selection, format consistency.
///
/// Phạm vi: TEST-PLAN.md #118 (LargeResult ContextCompression),
///         #125 (ToolResponse TokenEfficiency)
/// </summary>
public class ContextCompressorAiUxTests
{
    private readonly ContextCompressor _compressor = new();

    private static SearchResult MakeResult(int i, string? doc = null, string? summary = null) => new(
        Fqn: $"App.Module.Class{i}.Method{i}()",
        Name: $"Method{i}",
        Kind: "method",
        Signature: $"void Method{i}(int param{i})",
        FilePath: $"/repo/src/Module/Class{i}.cs",
        StartLine: i * 10,
        Score: 0.9 - (i * 0.01),
        Source: "Test",
        Documentation: doc,
        AiSummary: summary);

    // === #118: MCP_LargeResult_ContextCompression ===
    [Fact]
    public void Compress_100Results_FitsWithinTokenBudget()
    {
        // Mục đích: 100 results phải fit trong default 4000 token budget
        // (compression auto downgrade L2 → L1 → L0).
        var results = Enumerable.Range(1, 100).Select(i => MakeResult(i)).ToList();

        var compressed = _compressor.Compress(results);

        // Rough token estimate: 4 chars/token. Output phải dưới ~4000 tokens = ~16000 chars.
        var estimatedTokens = compressed.Length / 4;
        Assert.True(estimatedTokens <= 4500,  // 12.5% slack cho overhead
            $"Compressed 100 results = ~{estimatedTokens} tokens (budget: 4000)");
    }

    [Fact]
    public void Compress_FewResults_UsesL2DetailedFormat()
    {
        // Mục đích: 5 results với 4000 budget → 800 tokens/result → L2 (≥500 tokens).
        // L2 phải include Score và Source.
        var results = Enumerable.Range(1, 5).Select(i => MakeResult(i)).ToList();

        var compressed = _compressor.Compress(results);

        Assert.Contains("Score:", compressed);
        Assert.Contains("Test)", compressed); // Source field "Test"
        Assert.Contains("File:", compressed);
    }

    [Fact]
    public void Compress_ManyResults_DowngradesToL0Compact()
    {
        // Mục đích: 50 results với 4000 budget → 80 tokens/result → L0 (compact).
        // L0 không có "File:" hay "Score:" — chỉ kind + fqn + signature.
        var results = Enumerable.Range(1, 50).Select(i => MakeResult(i)).ToList();

        var compressed = _compressor.Compress(results);

        Assert.DoesNotContain("File:", compressed);
        Assert.DoesNotContain("Score:", compressed);
        // Vẫn có FQN.
        Assert.Contains("Method1", compressed);
    }

    [Fact]
    public void Compress_EmptyList_ReturnsEmptyString()
    {
        // Mục đích: Không có results → return empty string an toàn.
        var compressed = _compressor.Compress([]);
        Assert.Equal(string.Empty, compressed);
    }

    [Fact]
    public void Compress_SingleResult_UsesMostDetailedFormat()
    {
        // Mục đích: 1 result với 4000 budget → 4000 tokens/result → L2 với full detail.
        var result = MakeResult(42, doc: "This method processes payments", summary: "Payment handler");

        var compressed = _compressor.Compress([result]);

        Assert.Contains("App.Module.Class42.Method42()", compressed);
        Assert.Contains("Doc:", compressed); // L2 có Documentation
        Assert.Contains("Summary:", compressed); // L2 có AI Summary
    }

    // === #125: MCP_ToolResponse_TokenEfficiency ===
    [Fact]
    public void Compress_RespectsTokenBudgetExactly()
    {
        // Mục đích: Output phải KHÔNG vượt quá token budget.
        // Compress dừng append khi sẽ vượt budget.
        var results = Enumerable.Range(1, 200)
            .Select(i => MakeResult(i, doc: new string('x', 500))) // long docs
            .ToList();

        var compressed = _compressor.Compress(results, tokenBudget: 1000);
        var estimatedTokens = compressed.Length / 4;

        Assert.True(estimatedTokens <= 1100,  // 10% slack
            $"Compressed = ~{estimatedTokens} tokens, budget=1000");
    }

    [Fact]
    public void Compress_LongDocumentation_TruncatesToPreventOverflow()
    {
        // Mục đích: Documentation rất dài bị truncate thành "..." để không blow up.
        var hugeDoc = new string('x', 5000);
        var result = MakeResult(1, doc: hugeDoc);

        var compressed = _compressor.Compress([result]);

        Assert.Contains("xxx", compressed); // có doc
        Assert.DoesNotContain(hugeDoc, compressed); // KHÔNG full 5000 chars
        Assert.Contains("...", compressed); // truncation marker
    }

    // === #119: MCP_ResultFormat_ConsistentMarkdown ===
    [Fact]
    public void Compress_OutputFormat_ConsistentAcrossLevels()
    {
        // Mục đích: Mọi level đều bắt đầu với "[kind]" prefix → AI có thể parse consistent.
        var resultsL2 = new List<SearchResult> { MakeResult(1) };
        var resultsL0 = Enumerable.Range(1, 100).Select(i => MakeResult(i)).ToList();

        var l2Output = _compressor.Compress(resultsL2);
        var l0Output = _compressor.Compress(resultsL0);

        // Cả hai đều có "[method]" prefix.
        Assert.StartsWith("[method]", l2Output);
        Assert.StartsWith("[method]", l0Output);
    }
}
