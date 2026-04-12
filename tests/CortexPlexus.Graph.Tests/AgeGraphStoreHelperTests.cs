namespace CortexPlexus.Graph.Tests;

/// <summary>
/// Unit tests cho các static helper method internal của AgeGraphStore.
/// Không cần PostgreSQL — test pure logic (escape, sanitize, chunk).
///
/// Phạm vi: TEST-PLAN.md #1, #2, #3, #4
/// </summary>
public class AgeGraphStoreHelperTests
{
    // === #1: EscapeCypher_HandlesSpecialChars ===
    // Mục đích: Ngăn Cypher injection — single quote, backslash, newline phải được escape.

    [Fact]
    public void EscapeCypher_SingleQuote_IsEscaped()
    {
        // Single quote là ký tự nguy hiểm nhất vì Cypher string dùng '...'.
        var input = "O'Brien";
        var result = AgeGraphStore.EscapeCypher(input);
        Assert.Equal("O\\'Brien", result);
    }

    [Fact]
    public void EscapeCypher_Backslash_IsEscaped()
    {
        // Backslash phải được escape TRƯỚC single quote để không tạo escape sequence sai.
        var input = @"C:\path\to\file";
        var result = AgeGraphStore.EscapeCypher(input);
        Assert.Equal(@"C:\\path\\to\\file", result);
    }

    [Fact]
    public void EscapeCypher_BackslashThenQuote_BothEscaped()
    {
        // Edge case: backslash + quote phải được handle đúng thứ tự.
        var input = @"a\'b";
        var result = AgeGraphStore.EscapeCypher(input);
        // Backslash escape trước → "a\\'b", rồi quote escape → "a\\\\'b"... chờ, đây là bug tiềm năng
        // Implementation hiện tại: value.Replace("\\", "\\\\").Replace("'", "\\'")
        // Input "a\'b" → sau Replace("\\","\\\\") → "a\\'b" → sau Replace("'","\\'") → "a\\\\'b"
        // Đúng như expected — cả backslash gốc được double-escape, và quote được escape.
        Assert.Equal(@"a\\\'b", result);
    }

    [Fact]
    public void EscapeCypher_EmptyString_ReturnsEmpty()
    {
        var result = AgeGraphStore.EscapeCypher("");
        Assert.Equal("", result);
    }

    [Fact]
    public void EscapeCypher_NormalText_Unchanged()
    {
        // Text bình thường không bị thay đổi.
        var input = "MyClass.MyMethod";
        var result = AgeGraphStore.EscapeCypher(input);
        Assert.Equal("MyClass.MyMethod", result);
    }

    [Fact]
    public void EscapeCypher_SqlInjectionAttempt_IsNeutralized()
    {
        // Cypher injection attempt: kết thúc string sớm và inject DETACH DELETE.
        var input = "x'}) DETACH DELETE n //";
        var result = AgeGraphStore.EscapeCypher(input);
        // Single quote phải bị escape → không thể break out khỏi string literal Cypher.
        Assert.Equal("x\\'}) DETACH DELETE n //", result);
        // Mọi single quote trong output đều phải có backslash đứng ngay trước.
        for (var i = 0; i < result.Length; i++)
        {
            if (result[i] == '\'')
                Assert.True(i > 0 && result[i - 1] == '\\',
                    $"Unescaped quote at position {i}");
        }
    }

    // === #2: EscapeCypher_HandlesUnicode ===
    // Mục đích: Tiếng Việt, CJK, emoji trong symbol name không crash.

    [Fact]
    public void EscapeCypher_Vietnamese_Preserved()
    {
        var input = "XửLýThanhToán";
        var result = AgeGraphStore.EscapeCypher(input);
        Assert.Equal("XửLýThanhToán", result);
    }

    [Fact]
    public void EscapeCypher_ChineseCharacters_Preserved()
    {
        var input = "支付服务";
        var result = AgeGraphStore.EscapeCypher(input);
        Assert.Equal("支付服务", result);
    }

    [Fact]
    public void EscapeCypher_Emoji_Preserved()
    {
        // Emoji dùng trong tên file/comment không crash.
        var input = "PaymentService🔥";
        var result = AgeGraphStore.EscapeCypher(input);
        Assert.Equal("PaymentService🔥", result);
    }

    // === #3: SanitizeLabel_RemovesNonAlphanumeric ===
    // Mục đích: Label chứa ký tự đặc biệt không crash AGE.

    [Fact]
    public void SanitizeLabel_AlphanumericOnly_Unchanged()
    {
        var result = AgeGraphStore.SanitizeLabel("Method");
        Assert.Equal("Method", result);
    }

    [Fact]
    public void SanitizeLabel_WithUnderscore_Preserved()
    {
        // Underscore được phép.
        var result = AgeGraphStore.SanitizeLabel("api_endpoint");
        Assert.Equal("api_endpoint", result);
    }

    [Fact]
    public void SanitizeLabel_WithHyphen_Removed()
    {
        // Hyphen bị loại bỏ (AGE không cho phép).
        var result = AgeGraphStore.SanitizeLabel("di-registration");
        Assert.Equal("diregistration", result);
    }

    [Fact]
    public void SanitizeLabel_WithSpaces_Removed()
    {
        var result = AgeGraphStore.SanitizeLabel("Method With Spaces");
        Assert.Equal("MethodWithSpaces", result);
    }

    [Fact]
    public void SanitizeLabel_OnlySpecialChars_ReturnsUnknown()
    {
        // Edge case: input không có ký tự hợp lệ → fallback "Unknown".
        var result = AgeGraphStore.SanitizeLabel("!@#$%");
        Assert.Equal("Unknown", result);
    }

    [Fact]
    public void SanitizeLabel_Empty_ReturnsUnknown()
    {
        var result = AgeGraphStore.SanitizeLabel("");
        Assert.Equal("Unknown", result);
    }

    [Fact]
    public void SanitizeLabel_WithCypherInjection_Neutralized()
    {
        // Label injection attempt — các ký tự đặc biệt bị loại bỏ.
        var result = AgeGraphStore.SanitizeLabel("Method) DELETE (");
        Assert.Equal("MethodDELETE", result);
    }

    [Fact]
    public void SanitizeIdentifier_Empty_ReturnsProp()
    {
        // Identifier fallback là "prop" (khác với Label fallback "Unknown").
        var result = AgeGraphStore.SanitizeIdentifier("");
        Assert.Equal("prop", result);
    }

    [Fact]
    public void SanitizeIdentifier_ValidName_Unchanged()
    {
        var result = AgeGraphStore.SanitizeIdentifier("source_line");
        Assert.Equal("source_line", result);
    }

    // === #4: UpsertNodes_BatchesCorrectly ===
    // Mục đích: >100 items chia đúng batch 100/chunk (BatchSize hardcoded).
    // Test helper Chunk<T> trực tiếp thay vì gọi DB.

    [Fact]
    public void Chunk_EmptyList_ReturnsNoBatches()
    {
        var result = AgeGraphStore.Chunk(new List<int>(), 100).ToList();
        Assert.Empty(result);
    }

    [Fact]
    public void Chunk_LessThanBatchSize_SingleBatch()
    {
        var items = Enumerable.Range(1, 50).ToList();
        var batches = AgeGraphStore.Chunk(items, 100).ToList();

        Assert.Single(batches);
        Assert.Equal(50, batches[0].Count);
    }

    [Fact]
    public void Chunk_ExactlyBatchSize_SingleBatch()
    {
        var items = Enumerable.Range(1, 100).ToList();
        var batches = AgeGraphStore.Chunk(items, 100).ToList();

        Assert.Single(batches);
        Assert.Equal(100, batches[0].Count);
    }

    [Fact]
    public void Chunk_OverBatchSize_MultipleBatches()
    {
        // 250 items → 3 batches: [100, 100, 50]
        var items = Enumerable.Range(1, 250).ToList();
        var batches = AgeGraphStore.Chunk(items, 100).ToList();

        Assert.Equal(3, batches.Count);
        Assert.Equal(100, batches[0].Count);
        Assert.Equal(100, batches[1].Count);
        Assert.Equal(50, batches[2].Count);
    }

    [Fact]
    public void Chunk_PreservesOrder()
    {
        // Quan trọng: batches phải giữ đúng thứ tự gốc (relationship order matters).
        var items = Enumerable.Range(1, 250).ToList();
        var batches = AgeGraphStore.Chunk(items, 100).ToList();

        Assert.Equal(1, batches[0][0]);
        Assert.Equal(100, batches[0][99]);
        Assert.Equal(101, batches[1][0]);
        Assert.Equal(250, batches[2][49]);
    }

    // === Issue #4: Interface caller resolution helpers ===
    [Theory]
    [InlineData("App.Service.ProcessAsync()", "ProcessAsync")]
    [InlineData("App.Service.ProcessAsync", "ProcessAsync")]
    [InlineData("App.Service.ProcessAsync(int, string)", "ProcessAsync")]
    [InlineData("Foo.Bar.Baz.MethodName", "MethodName")]
    [InlineData("PlainMethod", "PlainMethod")]
    [InlineData("", "")]
    public void ExtractMethodNameFromFqn_HandlesAllVariants(string fqn, string expected)
    {
        Assert.Equal(expected, AgeGraphStore.ExtractMethodNameFromFqn(fqn));
    }

    [Theory]
    [InlineData("App.Service.ProcessAsync()", "App.Service")]
    [InlineData("App.Service.ProcessAsync", "App.Service")]
    [InlineData("App.Service.ProcessAsync(int)", "App.Service")]
    [InlineData("Foo.Bar.Baz.Method()", "Foo.Bar.Baz")]
    [InlineData("OnlyMethod", "")]  // no dot, no containing type
    [InlineData("", "")]
    public void ExtractContainingTypeFqn_StripsTrailingMethod(string methodFqn, string expected)
    {
        Assert.Equal(expected, AgeGraphStore.ExtractContainingTypeFqn(methodFqn));
    }

    // === Issue #C (R16): Route normalization for GetDataFlow ===
    [Theory]
    [InlineData("/api/chat/completion", "api/chat/completion")]
    [InlineData("/api/Chat/completion", "api/Chat/completion")]
    [InlineData("api/Chat/completion", "api/Chat/completion")]
    [InlineData("///api/users", "api/users")]  // multiple leading slashes
    [InlineData("  /api/users  ", "api/users")]  // with whitespace
    [InlineData("/", "")]  // just slash
    [InlineData("", "")]
    [InlineData("api", "api")]  // no slash
    public void NormalizeRoute_StripsLeadingSlashAndWhitespace(string input, string expected)
    {
        Assert.Equal(expected, AgeGraphStore.NormalizeRoute(input));
    }

    // === R25 R24-4: NormalizeGenericFqn collapses type arguments ===
    // Used by ExecuteCypherQuery to match call-site specializations like
    // `Foo.GetAsync<CognitiveDirectiveObject>` against the declaration row
    // `Foo.GetAsync<T>` in code_symbols.
    [Theory]
    [InlineData(
        "CortexFlow.Core.Interfaces.IPromptCacheService.GetAsync<CortexFlow.Core.DTOs.CognitiveDirectiveObject>",
        "CortexFlow.Core.Interfaces.IPromptCacheService.GetAsync<T>")]
    [InlineData("Foo.GetAsync<System.String>", "Foo.GetAsync<T>")]
    [InlineData("Foo.GetAsync<T>", "Foo.GetAsync<T>")]   // already declaration form
    [InlineData("Foo.NoGenerics", "Foo.NoGenerics")]      // unchanged when no <
    [InlineData("", "")]                                  // empty
    [InlineData("List<User>.Find", "List<T>.Find")]       // generic in middle
    [InlineData("Outer<A>.Inner<B>", "Outer<T>.Inner<T>")] // 2 separate generic groups
    public void NormalizeGenericFqn_CollapsesTypeArguments(string input, string expected)
    {
        Assert.Equal(expected, AgeGraphStore.NormalizeGenericFqn(input));
    }
}
