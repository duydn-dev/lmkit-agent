using LmKitOmniApi.Infrastructure.AI.Security;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LmKitOmniApi.Tests;

/// <summary>
/// Pure unit tests for <see cref="BrowserFetchExecutor"/> — NO real docker and NO real
/// process (execution is live-only; it needs a real headless-browser container). A fake
/// <see cref="IProcessRunner"/> records exactly how the executor invoked it (file name +
/// argument list + timeout) and returns a scripted <see cref="ProcessRunResult"/>, so we
/// can assert the container hardening flags, the DELIBERATE networked posture, the SSRF
/// gate firing BEFORE any launch, the host allowlist, output capping and the Vietnamese
/// failure-mode messages — without ever launching anything.
///
/// Hermetic-DNS note: the real <see cref="ToolSandboxService.ValidateUrlAsync"/> is used
/// (the contract requires calling it). Happy-path tests target a PUBLIC literal IP
/// (1.1.1.1), for which Dns.GetHostAddresses short-circuits and performs no network query;
/// SSRF-refusal tests target loopback/link-local literals that are rejected synchronously
/// before any DNS or process launch.
/// </summary>
public class BrowserFetchExecutorTests
{
    private static readonly Guid TenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid UserId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    // A public, hermetic (no real DNS) URL: an IP literal that is not private/loopback.
    private const string PublicUrl = "http://1.1.1.1/";

    // ─────────────────────────────────────────────
    // Fake runner + helpers
    // ─────────────────────────────────────────────

    private sealed class FakeProcessRunner : IProcessRunner
    {
        private readonly ProcessRunResult _result;

        public FakeProcessRunner(ProcessRunResult result) => _result = result;

        public int CallCount { get; private set; }
        public string? FileName { get; private set; }
        public IReadOnlyList<string>? Arguments { get; private set; }
        public string? Stdin { get; private set; }
        public TimeSpan Timeout { get; private set; }

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
            return Task.FromResult(_result);
        }
    }

    private static ProcessRunResult Success(string stdOut, string stdErr = "") =>
        new(ExitCode: 0, StdOut: stdOut, StdErr: stdErr, TimedOut: false);

    private static BrowserFetchOptions EnabledOptions(Action<BrowserFetchOptions>? tweak = null)
    {
        var options = new BrowserFetchOptions
        {
            Enabled = true,
            Image = "browserless/chrome:latest",
            RuntimePath = "docker",
            TimeoutSeconds = 30,
            MemoryMb = 512,
            Cpus = 1.0,
            MaxOutputChars = 12_000,
        };
        tweak?.Invoke(options);
        return options;
    }

    private static BrowserFetchExecutor CreateExecutor(BrowserFetchOptions options, IProcessRunner runner) =>
        new(Options.Create(options), runner, new ToolSandboxService(NullLogger<ToolSandboxService>.Instance),
            NullLogger<BrowserFetchExecutor>.Instance);

    /// <summary>Asserts <paramref name="flag"/> appears immediately followed by <paramref name="value"/>.</summary>
    private static void AssertFlagWithValue(IReadOnlyList<string> args, string flag, string value)
    {
        for (var i = 0; i < args.Count - 1; i++)
            if (args[i] == flag && args[i + 1] == value)
                return;

        Assert.Fail($"Expected argument '{flag}' immediately followed by '{value}'. " +
                    $"Actual: {string.Join(' ', args)}");
    }

    private static bool ContainsFlagWithValue(IReadOnlyList<string> args, string flag, string value)
    {
        for (var i = 0; i < args.Count - 1; i++)
            if (args[i] == flag && args[i + 1] == value)
                return true;
        return false;
    }

    // ─────────────────────────────────────────────
    // 1. IsEnabled reflects options — disabled never runs
    // ─────────────────────────────────────────────

    [Fact]
    public async Task Disabled_ReturnsNotConfigured_AndNeverCallsRunner()
    {
        var runner = new FakeProcessRunner(Success("hi"));
        var executor = CreateExecutor(EnabledOptions(o => o.Enabled = false), runner);

        var result = await executor.FetchAsync(PublicUrl, TenantId, UserId);

        Assert.Equal("[Browse] Công cụ duyệt web chưa được cấu hình.", result.Text);
        Assert.Null(result.ScreenshotFileId);
        Assert.False(executor.IsEnabled);
        Assert.Equal(0, runner.CallCount);
    }

    [Fact]
    public async Task EnabledButNoImage_IsDisabled_AndNeverCallsRunner()
    {
        var runner = new FakeProcessRunner(Success("hi"));
        var executor = CreateExecutor(EnabledOptions(o => o.Image = "   "), runner);

        var result = await executor.FetchAsync(PublicUrl, TenantId, UserId);

        Assert.Equal("[Browse] Công cụ duyệt web chưa được cấu hình.", result.Text);
        Assert.False(executor.IsEnabled);
        Assert.Equal(0, runner.CallCount);
    }

    [Fact]
    public void IsEnabled_TrueOnlyWhenEnabledAndImageConfigured()
    {
        var runner = new FakeProcessRunner(Success(""));
        Assert.True(CreateExecutor(EnabledOptions(), runner).IsEnabled);
        Assert.False(CreateExecutor(EnabledOptions(o => o.Enabled = false), runner).IsEnabled);
        Assert.False(CreateExecutor(EnabledOptions(o => o.Image = ""), runner).IsEnabled);
    }

    // ─────────────────────────────────────────────
    // 2. Happy path — hardened docker arguments
    // ─────────────────────────────────────────────

    [Fact]
    public async Task Enabled_HappyPath_ReturnsRenderedText_AndPassesHardenedArguments()
    {
        var runner = new FakeProcessRunner(Success("Rendered page text"));
        var options = EnabledOptions();
        var executor = CreateExecutor(options, runner);

        var result = await executor.FetchAsync(PublicUrl, TenantId, UserId);

        Assert.Equal("Rendered page text", result.Text);
        Assert.Null(result.ScreenshotFileId);          // v1 is text-only
        Assert.Equal(1, runner.CallCount);
        Assert.Equal("docker", runner.FileName);        // options.RuntimePath
        Assert.Null(runner.Stdin);                      // no stdin

        var args = runner.Arguments!;
        Assert.Equal("run", args[0]);
        Assert.Contains("--rm", args);
        Assert.Contains("--read-only", args);
        Assert.Contains("--pids-limit", args);
        Assert.Contains("browserless/chrome:latest", args);       // the configured image
        AssertFlagWithValue(args, "--cap-drop", "ALL");           // caps dropped
        AssertFlagWithValue(args, "--user", "65534:65534");       // non-root
        AssertFlagWithValue(args, "--memory", "512m");            // the configured MB
        AssertFlagWithValue(args, "--security-opt", "no-new-privileges");

        // The validated URL is the trailing positional argument (after the image).
        Assert.Equal(PublicUrl, args[^1]);
        Assert.Equal("browserless/chrome:latest", args[^2]);

        // Timeout passed to the runner = configured budget + spin-up grace (5s).
        Assert.Equal(TimeSpan.FromSeconds(options.TimeoutSeconds) + TimeSpan.FromSeconds(5), runner.Timeout);
    }

    [Fact]
    public async Task Enabled_HappyPath_DoesNotDisableNetwork_TheDeliberateDifference()
    {
        // Unlike the Python sandbox (--network none), the browser MUST have network egress,
        // so no --network flag is passed at all (docker's default bridge is networked).
        var runner = new FakeProcessRunner(Success("ok"));
        var executor = CreateExecutor(EnabledOptions(), runner);

        await executor.FetchAsync(PublicUrl, TenantId, UserId);

        Assert.False(ContainsFlagWithValue(runner.Arguments!, "--network", "none"));
        Assert.DoesNotContain("--network", runner.Arguments!);
    }

    [Fact]
    public async Task Enabled_EmptyOutput_ReturnsNoContentNotice()
    {
        var runner = new FakeProcessRunner(Success(string.Empty));
        var executor = CreateExecutor(EnabledOptions(), runner);

        var result = await executor.FetchAsync(PublicUrl, TenantId, UserId);

        Assert.Equal("(không có nội dung)", result.Text);
    }

    // ─────────────────────────────────────────────
    // 3. SSRF gate — refuses internal targets BEFORE any launch
    // ─────────────────────────────────────────────

    [Theory]
    [InlineData("http://127.0.0.1/admin")]
    [InlineData("http://169.254.169.254/latest/meta-data")]  // cloud metadata endpoint
    [InlineData("http://localhost:8080/")]
    [InlineData("file:///etc/passwd")]                        // unsupported scheme
    public async Task SsrfTarget_IsRefused_BeforeAnyProcessLaunch(string url)
    {
        var runner = new FakeProcessRunner(Success("should never run"));
        var executor = CreateExecutor(EnabledOptions(), runner);

        var result = await executor.FetchAsync(url, TenantId, UserId);

        Assert.StartsWith("[Browse] URL bị từ chối:", result.Text);
        Assert.Equal(0, runner.CallCount);   // the SSRF gate fired before launching the browser
    }

    // ─────────────────────────────────────────────
    // 4. Host allowlist
    // ─────────────────────────────────────────────

    [Fact]
    public async Task Host_NotOnAllowlist_IsRefused_BeforeAnyProcessLaunch()
    {
        var runner = new FakeProcessRunner(Success("should never run"));
        var executor = CreateExecutor(EnabledOptions(o => o.AllowedHosts.Add("example.com")), runner);

        var result = await executor.FetchAsync(PublicUrl, TenantId, UserId); // host 1.1.1.1 not allowed

        Assert.Contains("không nằm trong danh sách cho phép", result.Text);
        Assert.Equal(0, runner.CallCount);
    }

    [Fact]
    public async Task Host_OnAllowlist_IsPermitted()
    {
        var runner = new FakeProcessRunner(Success("allowed page"));
        var executor = CreateExecutor(EnabledOptions(o => o.AllowedHosts.Add("1.1.1.1")), runner);

        var result = await executor.FetchAsync(PublicUrl, TenantId, UserId);

        Assert.Equal("allowed page", result.Text);
        Assert.Equal(1, runner.CallCount);
    }

    // ─────────────────────────────────────────────
    // 5. Empty / oversized URL — never runs
    // ─────────────────────────────────────────────

    [Fact]
    public async Task EmptyUrl_ReturnsFriendlyMessage_AndNeverCallsRunner()
    {
        var runner = new FakeProcessRunner(Success("unused"));
        var executor = CreateExecutor(EnabledOptions(), runner);

        var result = await executor.FetchAsync("   ", TenantId, UserId);

        Assert.Equal("[Browse] Không có URL để truy cập.", result.Text);
        Assert.Equal(0, runner.CallCount);
    }

    // ─────────────────────────────────────────────
    // 6. Output capping (success path)
    // ─────────────────────────────────────────────

    [Fact]
    public async Task OversizedOutput_IsTruncatedAtCap_WithMarker()
    {
        var big = new string('x', 500);
        var runner = new FakeProcessRunner(Success(big));
        var executor = CreateExecutor(EnabledOptions(o => o.MaxOutputChars = 100), runner);

        var result = await executor.FetchAsync(PublicUrl, TenantId, UserId);

        Assert.StartsWith(new string('x', 100), result.Text);                             // first 100 kept
        Assert.Contains("[Nội dung đã bị cắt bớt vì vượt quá 100 ký tự]", result.Text);   // marker
        Assert.True(result.Text.Length < big.Length);
        Assert.DoesNotContain(new string('x', 200), result.Text);                         // tail dropped
    }

    // ─────────────────────────────────────────────
    // 7. Timeout
    // ─────────────────────────────────────────────

    [Fact]
    public async Task TimedOut_ReturnsTimeoutMessage()
    {
        var runner = new FakeProcessRunner(new ProcessRunResult(-1, string.Empty, string.Empty, TimedOut: true));
        var executor = CreateExecutor(EnabledOptions(o => o.TimeoutSeconds = 30), runner);

        var result = await executor.FetchAsync(PublicUrl, TenantId, UserId);

        Assert.Equal("[Browse] Truy cập vượt quá 30s và đã bị dừng.", result.Text);
    }

    // ─────────────────────────────────────────────
    // 8. Non-zero exit
    // ─────────────────────────────────────────────

    [Fact]
    public async Task NonZeroExit_WithStderr_ReturnsCappedErrorMessage()
    {
        var stderr = new string('E', 200);
        var runner = new FakeProcessRunner(new ProcessRunResult(1, string.Empty, stderr, TimedOut: false));
        var executor = CreateExecutor(EnabledOptions(o => o.MaxOutputChars = 50), runner);

        var result = await executor.FetchAsync(PublicUrl, TenantId, UserId);

        Assert.StartsWith("[Browse] Lỗi (exit 1):", result.Text);
        Assert.Contains("EEEEE", result.Text);                                       // stderr surfaced
        Assert.Contains("[Nội dung đã bị cắt bớt vì vượt quá 50 ký tự]", result.Text); // and capped
    }
}

/// <summary>
/// RBAC policy for the new "BrowseWeb" tool: granted to User/Admin (never Guest),
/// always approval-required (networked, side-effecting egress), and rate limited.
/// (Class name contains "Browse" so it is picked up by the chunk's test filter.)
/// </summary>
public class BrowseWebPermissionTests
{
    private static ToolPermissionService NewService() => new(NullLogger<ToolPermissionService>.Instance);

    [Fact]
    public async Task BrowseWeb_RequiresApproval_ForUserAndAdmin()
    {
        var permissions = NewService();

        var admin = await permissions.CanInvokeToolAsync(Guid.NewGuid(), Guid.NewGuid(), "Admin", "BrowseWeb");
        var user = await permissions.CanInvokeToolAsync(Guid.NewGuid(), Guid.NewGuid(), "User", "BrowseWeb");

        Assert.True(admin.RequiresApproval);
        Assert.True(user.RequiresApproval);
    }

    [Fact]
    public async Task BrowseWeb_IsDenied_ForGuest()
    {
        var permissions = NewService();

        var guest = await permissions.CanInvokeToolAsync(Guid.NewGuid(), Guid.NewGuid(), "Guest", "BrowseWeb");

        Assert.False(guest.IsAllowed);
        Assert.False(guest.RequiresApproval);
    }

    [Fact]
    public async Task BrowseWeb_IsListedForUserRole()
    {
        var permissions = NewService();

        var allowed = await permissions.GetAllowedToolsAsync("User");

        Assert.Contains("BrowseWeb", allowed);
    }
}
