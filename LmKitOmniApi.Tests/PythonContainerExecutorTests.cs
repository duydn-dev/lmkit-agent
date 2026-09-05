using LmKitOmniApi.Infrastructure.AI.Security;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LmKitOmniApi.Tests;

/// <summary>
/// Pure unit tests for <see cref="PythonContainerExecutor"/> — NO real docker and
/// NO real process. A fake <see cref="IProcessRunner"/> records exactly how the
/// executor invoked it (file name + argument list), can simulate the container
/// producing files under /work, and returns a scripted
/// <see cref="ProcessRunResult"/>, so we can assert the container hardening flags,
/// the Vietnamese failure-mode messages, output capping, scratch-dir lifecycle, and
/// produced-file harvesting — without ever launching anything.
/// </summary>
public class PythonContainerExecutorTests
{
    private static readonly Guid TenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid UserId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    // ─────────────────────────────────────────────
    // Fake runner + helpers
    // ─────────────────────────────────────────────

    /// <summary>
    /// Records the invocation and returns a fixed result. Also snapshots — at the
    /// moment it is called — whether the host scratch dir (the /work mount source)
    /// and the written main.py exist, proving the executor created and mounted them
    /// BEFORE the run (and, checked after ExecuteAsync, deleted them afterwards).
    /// The optional <paramref name="produceFiles"/> hook runs against the scratch dir
    /// during the call, simulating a container that wrote output files to /work.
    ///
    /// It also models daemon Docker for the timeout-teardown fix: on the primary
    /// <c>docker run</c> call it can write a container id to the host <c>--cidfile</c>
    /// (<paramref name="containerId"/>), simulating what the CLI does when the container
    /// starts. Follow-up calls whose first arg is NOT "run" (i.e. <c>docker kill</c> /
    /// <c>docker rm -f</c>) are recorded and answered with <paramref name="teardownExitCode"/>
    /// WITHOUT disturbing the recorded primary-run bookkeeping. When <paramref name="cancelOnRun"/>
    /// is supplied it is cancelled and an <see cref="OperationCanceledException"/> is thrown
    /// from the run call (after the cidfile is written), simulating caller cancellation.
    /// Every invocation is appended to <see cref="Calls"/>.
    /// </summary>
    private sealed class FakeProcessRunner : IProcessRunner
    {
        private readonly ProcessRunResult _result;
        private readonly Action<string>? _produceFiles;
        private readonly string? _containerId;
        private readonly int _teardownExitCode;
        private readonly CancellationTokenSource? _cancelOnRun;

        public FakeProcessRunner(
            ProcessRunResult result,
            Action<string>? produceFiles = null,
            string? containerId = null,
            int teardownExitCode = 0,
            CancellationTokenSource? cancelOnRun = null)
        {
            _result = result;
            _produceFiles = produceFiles;
            _containerId = containerId;
            _teardownExitCode = teardownExitCode;
            _cancelOnRun = cancelOnRun;
        }

        public int CallCount { get; private set; }
        public string? FileName { get; private set; }
        public IReadOnlyList<string>? Arguments { get; private set; }
        public string? Stdin { get; private set; }
        public TimeSpan Timeout { get; private set; }
        public string? ScratchDirAtCall { get; private set; }
        public bool ScratchDirExistedAtCall { get; private set; }
        public bool ScriptExistedAtCall { get; private set; }

        /// <summary>Every call, in order: the file name and its verbatim argument list.</summary>
        public List<(string FileName, IReadOnlyList<string> Arguments)> Calls { get; } = new();

        public Task<ProcessRunResult> RunAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            string? stdin,
            TimeSpan timeout,
            CancellationToken ct)
        {
            CallCount++;
            Calls.Add((fileName, arguments));

            // Follow-up teardown calls (docker kill / docker rm -f) — answer with the
            // scripted teardown exit code and leave the primary-run bookkeeping intact.
            if (arguments.Count == 0 || arguments[0] != "run")
                return Task.FromResult(new ProcessRunResult(_teardownExitCode, string.Empty, string.Empty, TimedOut: false));

            FileName = fileName;
            Arguments = arguments;
            Stdin = stdin;
            Timeout = timeout;

            ScratchDirAtCall = ExtractScratchDir(arguments);
            if (ScratchDirAtCall is not null)
            {
                ScratchDirExistedAtCall = Directory.Exists(ScratchDirAtCall);
                ScriptExistedAtCall = File.Exists(Path.Combine(ScratchDirAtCall, "main.py"));
                _produceFiles?.Invoke(ScratchDirAtCall);
            }

            // Simulate the docker CLI writing the started container id to --cidfile so the
            // executor's timeout/cancellation fallback can source the id and kill it.
            if (_containerId is not null)
            {
                var cidFile = ExtractCidFile(arguments);
                if (cidFile is not null)
                    File.WriteAllText(cidFile, _containerId);
            }

            // Simulate caller cancellation mid-run (AFTER the container "started"), so the
            // executor takes its cancellation path and still tears the container down.
            if (_cancelOnRun is not null)
            {
                _cancelOnRun.Cancel();
                throw new OperationCanceledException(_cancelOnRun.Token);
            }

            return Task.FromResult(_result);
        }
    }

    /// <summary>The host path passed as "--cidfile &lt;path&gt;", or null when absent.</summary>
    private static string? ExtractCidFile(IReadOnlyList<string> args)
    {
        for (var i = 0; i < args.Count - 1; i++)
            if (args[i] == "--cidfile")
                return args[i + 1];
        return null;
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

    private static UserResourceAccessService Resources() =>
        new(new ToolSandboxService(NullLogger<ToolSandboxService>.Instance));

    private static PythonContainerExecutor CreateExecutor(CodeInterpreterOptions options, IProcessRunner runner) =>
        new(Options.Create(options), runner, Resources(), NullLogger<PythonContainerExecutor>.Instance);

    /// <summary>Where produced files land for the test identity; cleaned up by file tests.</summary>
    private static string UploadDir() =>
        Path.Combine(Directory.GetCurrentDirectory(), "Uploads", TenantId.ToString("N"), UserId.ToString("N"));

    private static void CleanUploadDir()
    {
        var dir = UploadDir();
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

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

        var result = await executor.ExecuteAsync("print('hi')", TenantId, UserId);

        Assert.Equal("[Code Interpreter] Trình thông dịch Python chưa được cấu hình.", result.Output);
        Assert.Empty(result.Files);
        Assert.False(executor.IsEnabled);
        Assert.Equal(0, runner.CallCount);
    }

    [Fact]
    public async Task EnabledButNoImage_IsDisabled_AndNeverCallsRunner()
    {
        var runner = new FakeProcessRunner(Success("hi"));
        var executor = CreateExecutor(EnabledOptions(o => o.Image = "   "), runner);

        var result = await executor.ExecuteAsync("print('hi')", TenantId, UserId);

        Assert.Equal("[Code Interpreter] Trình thông dịch Python chưa được cấu hình.", result.Output);
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

        var result = await executor.ExecuteAsync("print('Xin chào')", TenantId, UserId);

        Assert.Equal("Xin chào", result.Output);
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

        await executor.ExecuteAsync("print('ok')", TenantId, UserId);

        AssertFlagWithValue(runner.Arguments!, "--network", "none");
    }

    [Fact]
    public async Task Enabled_HappyPath_MergesStderrUnderDivider()
    {
        var runner = new FakeProcessRunner(Success("stdout line", "a warning"));
        var executor = CreateExecutor(EnabledOptions(), runner);

        var result = await executor.ExecuteAsync("print('stdout line')", TenantId, UserId);

        Assert.Equal("stdout line\n--- stderr ---\na warning", result.Output);
    }

    [Fact]
    public async Task Enabled_EmptyOutput_ReturnsNoOutputNotice()
    {
        var runner = new FakeProcessRunner(Success(string.Empty));
        var executor = CreateExecutor(EnabledOptions(), runner);

        var result = await executor.ExecuteAsync("x = 1", TenantId, UserId);

        Assert.Equal("(không có đầu ra)", result.Output);
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
        var result = await executor.ExecuteAsync(oversized, TenantId, UserId);

        Assert.Equal("[Code Interpreter] Đoạn mã vượt quá giới hạn 100 ký tự.", result.Output);
        Assert.Equal(0, runner.CallCount);
    }

    [Fact]
    public async Task EmptyScript_ReturnsFriendlyMessage_AndNeverCallsRunner()
    {
        var runner = new FakeProcessRunner(Success("unused"));
        var executor = CreateExecutor(EnabledOptions(), runner);

        var result = await executor.ExecuteAsync("   ", TenantId, UserId);

        Assert.Equal("[Code Interpreter] Không có mã Python để thực thi.", result.Output);
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

        var result = await executor.ExecuteAsync("while True: pass", TenantId, UserId);

        Assert.Equal("[Code Interpreter] Thực thi vượt quá 15s và đã bị dừng.", result.Output);
    }

    // ─────────────────────────────────────────────
    // 4b. Container teardown — the CLI kill does NOT stop a daemon-managed
    //     container, so a --cidfile-sourced `docker kill` is the real backstop.
    // ─────────────────────────────────────────────

    [Fact]
    public async Task Enabled_HappyPath_PassesCidFileArgument_OutsideTheWorkMount()
    {
        var runner = new FakeProcessRunner(Success("ok"));
        var executor = CreateExecutor(EnabledOptions(), runner);

        await executor.ExecuteAsync("print('ok')", TenantId, UserId);

        // The run must carry "--cidfile <path>" so a timeout/cancel can source the id.
        var cidFile = ExtractCidFile(runner.Arguments!);
        Assert.False(string.IsNullOrWhiteSpace(cidFile), "The docker run must pass a --cidfile path.");

        // The cidfile must live OUTSIDE the /work mount source (a sibling), so it is
        // neither mounted into the container nor harvested as a produced file.
        var scratch = ExtractScratchDir(runner.Arguments!);
        Assert.NotNull(scratch);
        Assert.False(
            cidFile!.StartsWith(scratch! + Path.DirectorySeparatorChar, StringComparison.Ordinal),
            $"The cidfile '{cidFile}' must not live inside the /work mount source '{scratch}'.");
    }

    [Fact]
    public async Task TimedOut_IssuesDockerKill_WithContainerIdFromCidFile()
    {
        const string containerId = "abc123def4567890";
        // The fake writes this id to the run's --cidfile, exactly as the docker CLI would.
        var runner = new FakeProcessRunner(
            new ProcessRunResult(-1, string.Empty, string.Empty, TimedOut: true),
            containerId: containerId);
        var executor = CreateExecutor(EnabledOptions(o => o.TimeoutSeconds = 15), runner);

        var result = await executor.ExecuteAsync("while True: pass", TenantId, UserId);

        Assert.Equal("[Code Interpreter] Thực thi vượt quá 15s và đã bị dừng.", result.Output);

        // A follow-up `docker kill <id>` must have been issued via the SAME runner, with the
        // id sourced from the --cidfile (the executor has no other way to learn the id).
        var kill = Assert.Single(runner.Calls, c => c.Arguments.Count > 0 && c.Arguments[0] == "kill");
        Assert.Equal("docker", kill.FileName);
        Assert.Equal(new[] { "kill", containerId }, kill.Arguments.ToArray());

        // No force-remove needed when the kill succeeded (teardownExitCode defaults to 0).
        Assert.DoesNotContain(runner.Calls, c => c.Arguments.Count > 0 && c.Arguments[0] == "rm");
    }

    [Fact]
    public async Task TimedOut_WithoutContainerId_IssuesNoKill()
    {
        // The CLI never wrote a container id (container failed to start) → nothing to kill.
        var runner = new FakeProcessRunner(new ProcessRunResult(-1, string.Empty, string.Empty, TimedOut: true));
        var executor = CreateExecutor(EnabledOptions(), runner);

        await executor.ExecuteAsync("while True: pass", TenantId, UserId);

        Assert.Equal(1, runner.CallCount);   // only the docker run — no blind kill
        Assert.DoesNotContain(runner.Calls, c => c.Arguments.Count > 0 && c.Arguments[0] == "kill");
    }

    [Fact]
    public async Task TimedOut_KillDoesNotTake_FallsBackToForceRemove()
    {
        const string containerId = "cid_stuck_9";
        // teardownExitCode = 1 → the `docker kill` "fails", so `docker rm -f` must follow.
        var runner = new FakeProcessRunner(
            new ProcessRunResult(-1, string.Empty, string.Empty, TimedOut: true),
            containerId: containerId,
            teardownExitCode: 1);
        var executor = CreateExecutor(EnabledOptions(), runner);

        await executor.ExecuteAsync("while True: pass", TenantId, UserId);

        Assert.Contains(runner.Calls, c => c.Arguments.SequenceEqual(new[] { "kill", containerId }));
        Assert.Contains(runner.Calls, c => c.Arguments.SequenceEqual(new[] { "rm", "-f", containerId }));
    }

    [Fact]
    public async Task Canceled_IssuesDockerKill_ThenPropagates()
    {
        using var cts = new CancellationTokenSource();
        const string containerId = "cid_cancel_1";
        // The run writes the cidfile, cancels the token, then throws OperationCanceled.
        var runner = new FakeProcessRunner(
            Success("unused"),
            containerId: containerId,
            cancelOnRun: cts);
        var executor = CreateExecutor(EnabledOptions(), runner);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => executor.ExecuteAsync("print('x')", TenantId, UserId, cts.Token));

        // The cancellation path must ALSO tear the daemon-managed container down.
        var kill = Assert.Single(runner.Calls, c => c.Arguments.Count > 0 && c.Arguments[0] == "kill");
        Assert.Equal(new[] { "kill", containerId }, kill.Arguments.ToArray());
    }

    [Fact]
    public async Task SuccessfulRun_IssuesNoDockerKill()
    {
        // Even when a container id WAS written, a normal (non-timeout) run must not kill.
        var runner = new FakeProcessRunner(Success("ok"), containerId: "container_xyz");
        var executor = CreateExecutor(EnabledOptions(), runner);

        await executor.ExecuteAsync("print('ok')", TenantId, UserId);

        Assert.Equal(1, runner.CallCount);   // only the docker run
        Assert.DoesNotContain(runner.Calls, c => c.Arguments.Count > 0 && c.Arguments[0] == "kill");
        Assert.DoesNotContain(runner.Calls, c => c.Arguments.Count > 0 && c.Arguments[0] == "rm");
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

        var result = await executor.ExecuteAsync("raise SystemExit(1)", TenantId, UserId);

        Assert.StartsWith("[Code Interpreter] Lỗi (exit 1):", result.Output);
        Assert.Contains("EEEEE", result.Output);                                 // stderr surfaced
        Assert.Contains("[Kết quả đã bị cắt bớt vì vượt quá 50 ký tự]", result.Output);  // and capped
    }

    [Fact]
    public async Task NonZeroExit_NoStderr_FallsBackToStdout()
    {
        var runner = new FakeProcessRunner(new ProcessRunResult(2, "partial output before crash", string.Empty, false));
        var executor = CreateExecutor(EnabledOptions(), runner);

        var result = await executor.ExecuteAsync("import sys; sys.exit(2)", TenantId, UserId);

        Assert.StartsWith("[Code Interpreter] Lỗi (exit 2):", result.Output);
        Assert.Contains("partial output before crash", result.Output);
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

        var result = await executor.ExecuteAsync("print('x' * 500)", TenantId, UserId);

        Assert.StartsWith(new string('x', 100), result.Output);                          // first 100 kept
        Assert.Contains("[Kết quả đã bị cắt bớt vì vượt quá 100 ký tự]", result.Output); // marker
        Assert.True(result.Output.Length < big.Length, $"Result length {result.Output.Length} should be shorter than the raw 500.");
        Assert.DoesNotContain(new string('x', 200), result.Output);                      // tail dropped
    }

    // ─────────────────────────────────────────────
    // 7. Scratch-dir lifecycle
    // ─────────────────────────────────────────────

    [Fact]
    public async Task ScratchDirectory_IsCreatedAndMountedForRun_ThenDeleted()
    {
        var runner = new FakeProcessRunner(Success("done"));
        var executor = CreateExecutor(EnabledOptions(), runner);

        await executor.ExecuteAsync("print('done')", TenantId, UserId);

        Assert.NotNull(runner.ScratchDirAtCall);
        Assert.True(runner.ScratchDirExistedAtCall,
            "The host scratch dir (the /work mount source) must exist while the runner is invoked.");
        Assert.True(runner.ScriptExistedAtCall,
            "main.py must be written into the scratch dir before the run.");
        Assert.False(Directory.Exists(runner.ScratchDirAtCall!),
            "The scratch dir must be deleted after ExecuteAsync returns.");
    }

    // ─────────────────────────────────────────────
    // 8. Produced-file harvesting
    // ─────────────────────────────────────────────

    [Fact]
    public async Task ProducedFile_IsHarvested_PersistedAndDescribed_ExcludingMainPy()
    {
        CleanUploadDir();
        try
        {
            var pngBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3 };
            var runner = new FakeProcessRunner(
                Success("chart written"),
                produceFiles: scratch => File.WriteAllBytes(Path.Combine(scratch, "chart.png"), pngBytes));
            var executor = CreateExecutor(EnabledOptions(), runner);

            var result = await executor.ExecuteAsync("save chart", TenantId, UserId);

            Assert.Equal("chart written", result.Output);
            var file = Assert.Single(result.Files);
            Assert.Equal("chart.png", file.Name);              // original name preserved for display
            Assert.NotEqual("chart.png", file.Id);             // stored under a server-generated name
            Assert.EndsWith(".png", file.Id);                  // extension retained for content typing
            Assert.Equal("image/png", file.ContentType);
            Assert.Equal(pngBytes.Length, file.SizeBytes);

            // Persisted into the caller's isolated upload root, readable by id, unchanged.
            var stored = Path.Combine(UploadDir(), file.Id);
            Assert.True(File.Exists(stored), "The produced file must be persisted under the owner upload dir.");
            Assert.Equal(pngBytes, await File.ReadAllBytesAsync(stored));

            // The scratch dir (with main.py) is still cleaned up afterwards.
            Assert.False(Directory.Exists(runner.ScratchDirAtCall!));
        }
        finally
        {
            CleanUploadDir();
        }
    }

    [Fact]
    public async Task ProducedFiles_AreCappedByMaxOutputFiles()
    {
        CleanUploadDir();
        try
        {
            var runner = new FakeProcessRunner(
                Success("ok"),
                produceFiles: scratch =>
                {
                    for (var i = 0; i < 5; i++)
                        File.WriteAllText(Path.Combine(scratch, $"out{i}.txt"), "data");
                });
            var executor = CreateExecutor(EnabledOptions(o => o.MaxOutputFiles = 2), runner);

            var result = await executor.ExecuteAsync("write many", TenantId, UserId);

            Assert.Equal(2, result.Files.Count);
        }
        finally
        {
            CleanUploadDir();
        }
    }

    [Fact]
    public async Task ProducedFiles_OverPerFileSizeCap_AreSkipped()
    {
        CleanUploadDir();
        try
        {
            var runner = new FakeProcessRunner(
                Success("ok"),
                produceFiles: scratch =>
                {
                    File.WriteAllBytes(Path.Combine(scratch, "small.bin"), new byte[10]);
                    File.WriteAllBytes(Path.Combine(scratch, "big.bin"), new byte[100]);
                });
            var executor = CreateExecutor(EnabledOptions(o => o.MaxOutputFileBytes = 50), runner);

            var result = await executor.ExecuteAsync("write two", TenantId, UserId);

            var file = Assert.Single(result.Files);
            Assert.Equal("small.bin", file.Name);
        }
        finally
        {
            CleanUploadDir();
        }
    }

    [Fact]
    public async Task ProducedFiles_AreNotReturned_WhenDisabledByCap()
    {
        CleanUploadDir();
        try
        {
            var runner = new FakeProcessRunner(
                Success("ok"),
                produceFiles: scratch => File.WriteAllText(Path.Combine(scratch, "out.txt"), "data"));
            var executor = CreateExecutor(EnabledOptions(o => o.MaxOutputFiles = 0), runner);

            var result = await executor.ExecuteAsync("write one", TenantId, UserId);

            Assert.Empty(result.Files);
        }
        finally
        {
            CleanUploadDir();
        }
    }

    [Fact]
    public async Task FailedRun_ReturnsNoFiles_EvenWhenScratchHasThem()
    {
        CleanUploadDir();
        try
        {
            var runner = new FakeProcessRunner(
                new ProcessRunResult(1, string.Empty, "boom", TimedOut: false),
                produceFiles: scratch => File.WriteAllText(Path.Combine(scratch, "partial.txt"), "half"));
            var executor = CreateExecutor(EnabledOptions(), runner);

            var result = await executor.ExecuteAsync("crash after writing", TenantId, UserId);

            Assert.StartsWith("[Code Interpreter] Lỗi (exit 1):", result.Output);
            Assert.Empty(result.Files);
        }
        finally
        {
            CleanUploadDir();
        }
    }
}
