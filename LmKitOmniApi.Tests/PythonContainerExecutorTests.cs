using LmKitOmniApi.Infrastructure.AI.Security;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LmKitOmniApi.Tests;

/// <summary>
/// Pure unit tests for <see cref="PythonContainerExecutor"/> — NO real docker and
/// NO real process. A fake <see cref="IProcessRunner"/> records exactly how the
/// executor invoked it (file name + argument list) and returns a scripted
/// <see cref="ProcessRunResult"/>, so we can assert the container hardening flags,
/// the Vietnamese failure-mode messages, output capping, and scratch-dir lifecycle
/// without ever launching anything.
/// </summary>
public class PythonContainerExecutorTests
{
    // ─────────────────────────────────────────────
    // Fake runner + helpers
    // ─────────────────────────────────────────────

    /// <summary>
    /// Records the invocation and returns a fixed result. Also snapshots — at the
    /// moment it is called — whether the host scratch dir (the /work mount source)
    /// and the written main.py exist, proving the executor created and mounted them
    /// BEFORE the run (and, checked after ExecuteAsync, deleted them afterwards).
    /// </summary>
    private sealed class FakeProcessRunner : IProcessRunner
    {
        private readonly ProcessRunResult _result;

        public FakeProcessRunner(ProcessRunResult result) => _result = result;

        public int CallCount { get; private set; }
        public string? FileName { get; private set; }
        public IReadOnlyList<string>? Arguments { get; private set; }
        public string? Stdin { get; private set; }
        public TimeSpan Timeout { get; private set; }
        public string? ScratchDirAtCall { get; private set; }
        public bool ScratchDirExistedAtCall { get; private set; }
        public bool ScriptExistedAtCall { get; private set; }

        public Task<ProcessRunResult> RunAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            string? stdin,
            TimeSpan timeout,
            CancellationToken ct)
        {
            CallCount++;
            FileName = fileName;
            Arguments = arguments;
            Stdin = stdin;
            Timeout = timeout;

            ScratchDirAtCall = ExtractScratchDir(arguments);
            if (ScratchDirAtCall is not null)
            {
                ScratchDirExistedAtCall = Directory.Exists(ScratchDirAtCall);
                ScriptExistedAtCall = File.Exists(Path.Combine(ScratchDirAtCall, "main.py"));
            }

            return Task.FromResult(_result);
        }
    }

    private static ProcessRunResult Success(string stdOut, string stdErr = "") =>
        new(ExitCode: 0, StdOut: stdOut, StdErr: stdErr, TimedOut: false);

    private static CodeInterpreterOptions EnabledOptions(Action<CodeInterpreterOptions>? tweak = null)
    {
        var options = new CodeInterpreterOptions
        {
            Enabled = true,
            Image = "python:3.12-alpine",
            RuntimePath = "docker",
            TimeoutSeconds = 15,
            MemoryMb = 256,
            Cpus = 1.0,
            MaxOutputChars = 8_000,
            MaxScriptChars = 20_000,
        };
        tweak?.Invoke(options);
        return options;
    }

    private static PythonContainerExecutor CreateExecutor(CodeInterpreterOptions options, IProcessRunner runner) =>
        new(Options.Create(options), runner, NullLogger<PythonContainerExecutor>.Instance);

    /// <summary>The host side of the "--volume {host}:/work:rw" mount (Windows drive-letter safe).</summary>
    private static string? ExtractScratchDir(IReadOnlyList<string> args)
    {
        const string suffix = ":/work:rw";
        for (var i = 0; i < args.Count - 1; i++)
        {
            if (args[i] != "--volume") continue;
            var mount = args[i + 1];
            return mount.EndsWith(suffix, StringComparison.Ordinal)
                ? mount[..^suffix.Length]
                : mount;
        }
        return null;
    }

    /// <summary>Asserts <paramref name="flag"/> appears immediately followed by <paramref name="value"/>.</summary>
    private static void AssertFlagWithValue(IReadOnlyList<string> args, string flag, string value)
    {
        for (var i = 0; i < args.Count - 1; i++)
            if (args[i] == flag && args[i + 1] == value)
                return;

        Assert.Fail($"Expected argument '{flag}' immediately followed by '{value}'. " +
                    $"Actual: {string.Join(' ', args)}");
    }

    // ─────────────────────────────────────────────
    // 1. Disabled — never runs
    // ─────────────────────────────────────────────

    [Fact]
    public async Task Disabled_ReturnsNotConfigured_AndNeverCallsRunner()
    {
        var runner = new FakeProcessRunner(Success("hi"));
        var executor = CreateExecutor(EnabledOptions(o => o.Enabled = false), runner);

        var result = await executor.ExecuteAsync("print('hi')");

        Assert.Equal("[Code Interpreter] Trình thông dịch Python chưa được cấu hình.", result);
        Assert.False(executor.IsEnabled);
        Assert.Equal(0, runner.CallCount);
    }

    [Fact]
    public async Task EnabledButNoImage_IsDisabled_AndNeverCallsRunner()
    {
        var runner = new FakeProcessRunner(Success("hi"));
        var executor = CreateExecutor(EnabledOptions(o => o.Image = "   "), runner);

        var result = await executor.ExecuteAsync("print('hi')");

        Assert.Equal("[Code Interpreter] Trình thông dịch Python chưa được cấu hình.", result);
        Assert.False(executor.IsEnabled);
        Assert.Equal(0, runner.CallCount);
    }

    // ─────────────────────────────────────────────
    // 2. Happy path — hardened docker arguments
    // ─────────────────────────────────────────────

    [Fact]
    public async Task Enabled_HappyPath_ReturnsStdout_AndPassesHardenedArguments()
    {
        var runner = new FakeProcessRunner(Success("Xin chào"));
        var executor = CreateExecutor(EnabledOptions(), runner);

        var result = await executor.ExecuteAsync("print('Xin chào')");

        Assert.Equal("Xin chào", result);
        Assert.Equal(1, runner.CallCount);
        Assert.Equal("docker", runner.FileName);           // options.RuntimePath
        Assert.Null(runner.Stdin);                         // v1 feeds no stdin

        var args = runner.Arguments!;
        Assert.Equal("run", args[0]);
        Assert.Contains("--rm", args);
        Assert.Contains("--read-only", args);
        Assert.Contains("--pids-limit", args);
        Assert.Contains("python:3.12-alpine", args);       // the configured image
        AssertFlagWithValue(args, "--cap-drop", "ALL");
        AssertFlagWithValue(args, "--user", "65534:65534");
        AssertFlagWithValue(args, "--memory", "256m");     // the configured MB
        AssertFlagWithValue(args, "--security-opt", "no-new-privileges");

        // python /work/main.py — the interpreter and a path ending in main.py.
        var scriptLaunched = false;
        for (var i = 0; i < args.Count - 1; i++)
            if (args[i] == "python" && args[i + 1].EndsWith("main.py", StringComparison.Ordinal))
            {
                scriptLaunched = true;
                break;
            }
        Assert.True(scriptLaunched, "Expected 'python' followed by a path ending in main.py.");
    }

    [Fact]
    public async Task Enabled_HappyPath_DisablesNetwork_TheKeySsrfGuard()
    {
        // Dedicated assertion: --network none is the primary SSRF / exfiltration guard.
        var runner = new FakeProcessRunner(Success("ok"));
        var executor = CreateExecutor(EnabledOptions(), runner);

        await executor.ExecuteAsync("print('ok')");

        AssertFlagWithValue(runner.Arguments!, "--network", "none");
    }

    [Fact]
    public async Task Enabled_HappyPath_MergesStderrUnderDivider()
    {
        var runner = new FakeProcessRunner(Success("stdout line", "a warning"));
        var executor = CreateExecutor(EnabledOptions(), runner);

        var result = await executor.ExecuteAsync("print('stdout line')");

        Assert.Equal("stdout line\n--- stderr ---\na warning", result);
    }

    [Fact]
    public async Task Enabled_EmptyOutput_ReturnsNoOutputNotice()
    {
        var runner = new FakeProcessRunner(Success(string.Empty));
        var executor = CreateExecutor(EnabledOptions(), runner);

        var result = await executor.ExecuteAsync("x = 1");

        Assert.Equal("(không có đầu ra)", result);
    }

    // ─────────────────────────────────────────────
    // 3. Script-size rejection — never runs
    // ─────────────────────────────────────────────

    [Fact]
    public async Task OverLimitScript_ReturnsFriendlyMessage_AndNeverCallsRunner()
    {
        var runner = new FakeProcessRunner(Success("unused"));
        var executor = CreateExecutor(EnabledOptions(o => o.MaxScriptChars = 100), runner);

        var oversized = new string('a', 101);
        var result = await executor.ExecuteAsync(oversized);

        Assert.Equal("[Code Interpreter] Đoạn mã vượt quá giới hạn 100 ký tự.", result);
        Assert.Equal(0, runner.CallCount);
    }

    [Fact]
    public async Task EmptyScript_ReturnsFriendlyMessage_AndNeverCallsRunner()
    {
        var runner = new FakeProcessRunner(Success("unused"));
        var executor = CreateExecutor(EnabledOptions(), runner);

        var result = await executor.ExecuteAsync("   ");

        Assert.Equal("[Code Interpreter] Không có mã Python để thực thi.", result);
        Assert.Equal(0, runner.CallCount);
    }

    // ─────────────────────────────────────────────
    // 4. Timeout
    // ─────────────────────────────────────────────

    [Fact]
    public async Task TimedOut_ReturnsTimeoutMessage()
    {
        var runner = new FakeProcessRunner(new ProcessRunResult(-1, string.Empty, string.Empty, TimedOut: true));
        var executor = CreateExecutor(EnabledOptions(o => o.TimeoutSeconds = 15), runner);

        var result = await executor.ExecuteAsync("while True: pass");

        Assert.Equal("[Code Interpreter] Thực thi vượt quá 15s và đã bị dừng.", result);
    }

    // ─────────────────────────────────────────────
    // 5. Non-zero exit
    // ─────────────────────────────────────────────

    [Fact]
    public async Task NonZeroExit_WithStderr_ReturnsCappedErrorMessage()
    {
        var stderr = new string('E', 200);
        var runner = new FakeProcessRunner(new ProcessRunResult(1, string.Empty, stderr, TimedOut: false));
        var executor = CreateExecutor(EnabledOptions(o => o.MaxOutputChars = 50), runner);

        var result = await executor.ExecuteAsync("raise SystemExit(1)");

        Assert.StartsWith("[Code Interpreter] Lỗi (exit 1):", result);
        Assert.Contains("EEEEE", result);                                 // stderr surfaced
        Assert.Contains("[Kết quả đã bị cắt bớt vì vượt quá 50 ký tự]", result);  // and capped
    }

    [Fact]
    public async Task NonZeroExit_NoStderr_FallsBackToStdout()
    {
        var runner = new FakeProcessRunner(new ProcessRunResult(2, "partial output before crash", string.Empty, false));
        var executor = CreateExecutor(EnabledOptions(), runner);

        var result = await executor.ExecuteAsync("import sys; sys.exit(2)");

        Assert.StartsWith("[Code Interpreter] Lỗi (exit 2):", result);
        Assert.Contains("partial output before crash", result);
    }

    // ─────────────────────────────────────────────
    // 6. Output capping (success path)
    // ─────────────────────────────────────────────

    [Fact]
    public async Task OversizedStdout_IsTruncatedAtCap_WithMarker()
    {
        var big = new string('x', 500);
        var runner = new FakeProcessRunner(Success(big));
        var executor = CreateExecutor(EnabledOptions(o => o.MaxOutputChars = 100), runner);

        var result = await executor.ExecuteAsync("print('x' * 500)");

        Assert.StartsWith(new string('x', 100), result);                          // first 100 kept
        Assert.Contains("[Kết quả đã bị cắt bớt vì vượt quá 100 ký tự]", result); // marker
        Assert.True(result.Length < big.Length, $"Result length {result.Length} should be shorter than the raw 500.");
        Assert.DoesNotContain(new string('x', 200), result);                      // tail dropped
    }

    // ─────────────────────────────────────────────
    // 7. Scratch-dir lifecycle
    // ─────────────────────────────────────────────

    [Fact]
    public async Task ScratchDirectory_IsCreatedAndMountedForRun_ThenDeleted()
    {
        var runner = new FakeProcessRunner(Success("done"));
        var executor = CreateExecutor(EnabledOptions(), runner);

        await executor.ExecuteAsync("print('done')");

        Assert.NotNull(runner.ScratchDirAtCall);
        Assert.True(runner.ScratchDirExistedAtCall,
            "The host scratch dir (the /work mount source) must exist while the runner is invoked.");
        Assert.True(runner.ScriptExistedAtCall,
            "main.py must be written into the scratch dir before the run.");
        Assert.False(Directory.Exists(runner.ScratchDirAtCall!),
            "The scratch dir must be deleted after ExecuteAsync returns.");
    }
}
