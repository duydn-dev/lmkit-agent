using LmKitOmniApi.Infrastructure.AI.Security;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LmKitOmniApi.Tests;

/// <summary>
/// Pure unit tests for <see cref="ExecutionSandboxEngine"/> — no model, no
/// network: everything runs inside the in-process Jint sandbox. Covers the v1
/// code-interpreter contract: last-expression result, captured console output,
/// resource-limit termination, language rejection, result-size capping and the
/// no-CLR guarantee.
/// </summary>
public class ExecutionSandboxEngineTests
{
    /// <summary>Generous upper bound for tests that rely on the engine's own 2s kill-switch.</summary>
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(30);

    private static ExecutionSandboxEngine CreateEngine(bool javaScriptEnabled = true) =>
        new(Options.Create(new CodeInterpreterOptions { JavaScriptEnabled = javaScriptEnabled }),
            NullLogger<ExecutionSandboxEngine>.Instance);

    // ─────────────────────────────────────────────
    // 1. Results: last expression, objects, no value
    // ─────────────────────────────────────────────

    [Fact]
    public async Task ArithmeticExpression_ReturnsLastExpressionValue()
    {
        var result = await CreateEngine().ExecuteCodeSafelyAsync(
            "const a = 6; const b = 7; a * b;", "javascript");

        Assert.Equal("42", result);
    }

    [Fact]
    public async Task JsLanguageAlias_IsAccepted()
    {
        var result = await CreateEngine().ExecuteCodeSafelyAsync("1 + 2", "JS");

        Assert.Equal("3", result);
    }

    [Fact]
    public async Task ArrayResult_IsJsonSerialized()
    {
        var result = await CreateEngine().ExecuteCodeSafelyAsync(
            "[1, 2, 3].map(x => x * 2)", "javascript");

        Assert.Equal("[2,4,6]", result);
    }

    [Fact]
    public async Task ObjectResult_IsJsonSerialized()
    {
        var result = await CreateEngine().ExecuteCodeSafelyAsync(
            "({ name: 'An', tuoi: 3 })", "javascript");

        Assert.Equal("{\"name\":\"An\",\"tuoi\":3}", result);
    }

    [Theory]
    [InlineData("undefined")]
    [InlineData("null")]
    [InlineData("var x = 1;")]
    public async Task UndefinedOrNullResult_ReturnsExplicitNotice(string code)
    {
        var result = await CreateEngine().ExecuteCodeSafelyAsync(code, "javascript");

        Assert.Equal("(không có giá trị trả về)", result);
    }

    // ─────────────────────────────────────────────
    // 2. Console capture
    // ─────────────────────────────────────────────

    [Fact]
    public async Task ConsoleOutput_IsCapturedAlongsideResult()
    {
        var result = await CreateEngine().ExecuteCodeSafelyAsync(
            "console.log('xin chào', 1 + 1); console.warn('cảnh báo'); 'done'", "javascript");

        Assert.Contains("xin chào 2", result);
        Assert.Contains("[warn] cảnh báo", result);
        Assert.Contains("\n---\n", result);
        Assert.EndsWith("done", result);
    }

    [Fact]
    public async Task ConsoleOnlyScript_ReportsNoReturnValueAfterSeparator()
    {
        var result = await CreateEngine().ExecuteCodeSafelyAsync(
            "console.log('chỉ log thôi')", "javascript");

        Assert.Equal("chỉ log thôi\n---\n(không có giá trị trả về)", result);
    }

    [Fact]
    public async Task WithoutConsoleOutput_NoSeparatorIsEmitted()
    {
        var result = await CreateEngine().ExecuteCodeSafelyAsync("40 + 2", "javascript");

        Assert.Equal("42", result);
        Assert.DoesNotContain("---", result);
    }

    [Fact]
    public async Task ConsoleFlood_IsBoundedByBuffer()
    {
        var result = await CreateEngine().ExecuteCodeSafelyAsync(
            "for (let i = 0; i < 300; i++) { console.log('dòng ' + i); } 'ok'", "javascript");

        Assert.Contains("vượt giới hạn bộ đệm", result);
        Assert.DoesNotContain("dòng 299", result);
        Assert.EndsWith("ok", result);
    }

    // ─────────────────────────────────────────────
    // 3. Resource limits & termination
    // ─────────────────────────────────────────────

    [Fact]
    public async Task InfiniteLoop_IsTerminatedByLimits_NotHanging()
    {
        var task = CreateEngine().ExecuteCodeSafelyAsync("while (true) { }", "javascript");
        var completed = await Task.WhenAny(task, Task.Delay(TestTimeout));

        Assert.Same(task, completed);
        Assert.StartsWith("[Sandbox Error]", await task);
    }

    [Fact]
    public async Task OversizedResult_IsTruncatedAtCap()
    {
        var result = await CreateEngine().ExecuteCodeSafelyAsync("'x'.repeat(20000)", "javascript");

        Assert.Contains("[Kết quả đã bị cắt bớt", result);
        Assert.True(result.Length < 8_200,
            $"Result length {result.Length} exceeds the 8,000-char cap plus marker.");
    }

    [Fact]
    public async Task PreCanceledToken_ThrowsOperationCanceled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => CreateEngine().ExecuteCodeSafelyAsync("1 + 1", "javascript", cts.Token));
    }

    // ─────────────────────────────────────────────
    // 4. Rejections & failure modes
    // ─────────────────────────────────────────────

    [Theory]
    [InlineData("python")]
    [InlineData("csharp")]
    [InlineData("bash")]
    public async Task NonJavaScriptLanguage_IsRejected(string language)
    {
        var result = await CreateEngine().ExecuteCodeSafelyAsync("print('hi')", language);

        Assert.Equal("[Sandbox Error] Chỉ hỗ trợ thực thi an toàn ngôn ngữ JavaScript (qua Jint).", result);
    }

    [Fact]
    public async Task RuntimeJavaScriptError_SurfacesMessageAsErrorString()
    {
        var result = await CreateEngine().ExecuteCodeSafelyAsync(
            "throw new Error('nổ tung');", "javascript");

        Assert.StartsWith("[Sandbox Error]", result);
        Assert.Contains("nổ tung", result);
    }

    [Fact]
    public async Task SyntaxError_ReturnsSandboxErrorString()
    {
        var result = await CreateEngine().ExecuteCodeSafelyAsync("const = ;;;(", "javascript");

        Assert.StartsWith("[Sandbox Error]", result);
    }

    // ─────────────────────────────────────────────
    // 5. CLR isolation
    // ─────────────────────────────────────────────

    [Fact]
    public async Task ClrNamespaces_AreNotExposedToScripts()
    {
        var probe = await CreateEngine().ExecuteCodeSafelyAsync(
            "(typeof System === 'undefined' && typeof importNamespace === 'undefined') ? 'no-clr' : 'clr-exposed'",
            "javascript");

        Assert.Equal("no-clr", probe);
    }

    [Fact]
    public async Task ClrAccessAttempt_FailsSafely()
    {
        var result = await CreateEngine().ExecuteCodeSafelyAsync(
            "System.IO.File.ReadAllText('C:/windows/win.ini')", "javascript");

        Assert.StartsWith("[Sandbox Error]", result);
    }

    // ─────────────────────────────────────────────
    // 6. Feature toggle (CodeInterpreterOptions.JavaScriptEnabled)
    // ─────────────────────────────────────────────

    [Fact]
    public void JavaScriptEnabled_DefaultsToTrue_PreservingBehavior()
    {
        // The option defaults to true so existing behavior (Jint always available) is kept.
        Assert.True(new CodeInterpreterOptions().JavaScriptEnabled);
    }

    [Fact]
    public void IsEnabled_IsTrueByDefault()
    {
        Assert.True(CreateEngine().IsEnabled);
    }

    [Fact]
    public void IsEnabled_IsFalse_WhenJavaScriptDisabled()
    {
        Assert.False(CreateEngine(javaScriptEnabled: false).IsEnabled);
    }

    [Fact]
    public async Task Disabled_ReturnsNotEnabledMessage_WithoutExecuting()
    {
        // Mirrors the Python path when off: a safe bracketed message, never execution.
        var result = await CreateEngine(javaScriptEnabled: false)
            .ExecuteCodeSafelyAsync("1 + 1", "javascript");

        Assert.Equal("[Sandbox Error] Trình thông dịch JavaScript chưa được bật.", result);
    }
}
