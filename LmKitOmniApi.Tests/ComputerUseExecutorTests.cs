using LmKitOmniApi.Infrastructure.AI.ComputerUse;
using LmKitOmniApi.Infrastructure.AI.Security;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LmKitOmniApi.Tests;

/// <summary>
/// Pure unit tests for <see cref="ComputerUseExecutor"/> — NO real docker and NO real
/// process (execution is live-only; it needs a real interactive-browser container). A fake
/// <see cref="IProcessRunner"/> records exactly how the executor invoked it (file name +
/// argument list + timeout), can simulate the container writing a screenshot into the
/// session dir, and returns a scripted <see cref="ProcessRunResult"/>. That lets us assert
/// the container hardening flags, the net-allowlist construction, the SSRF gate firing
/// BEFORE any launch (navigate only), the strict deny-all allowlist, observation parsing,
/// and screenshot harvesting — without ever launching anything.
///
/// Hermetic-DNS note: the real <see cref="ToolSandboxService.ValidateUrlAsync"/> is used
/// (the contract requires it). Happy-path navigation targets the PUBLIC literal IP 1.1.1.1
/// (Dns short-circuits, no network query); SSRF tests target loopback/link-local literals
/// rejected synchronously before any DNS or launch.
/// </summary>
public class ComputerUseExecutorTests
{
    private static readonly Guid TenantId = Guid.Parse("aaaaaaaa-1111-1111-1111-111111111111");
    private static readonly Guid UserId = Guid.Parse("bbbbbbbb-2222-2222-2222-222222222222");

    private const string PublicUrl = "http://1.1.1.1/";

    private sealed class FakeProcessRunner : IProcessRunner
    {
        private readonly ProcessRunResult _result;
        private readonly Action<string>? _produce;

        public FakeProcessRunner(ProcessRunResult result, Action<string>? produce = null)
        {
            _result = result;
            _produce = produce;
        }

        public int CallCount { get; private set; }
        public string? FileName { get; private set; }
        public IReadOnlyList<string>? Arguments { get; private set; }
        public TimeSpan Timeout { get; private set; }

        public Task<ProcessRunResult> RunAsync(
            string fileName, IReadOnlyList<string> arguments, string? stdin, TimeSpan timeout, CancellationToken ct)
        {
            CallCount++;
            FileName = fileName;
            Arguments = arguments;
            Timeout = timeout;
            var sessionDir = ExtractSessionDir(arguments);
            if (sessionDir is not null) _produce?.Invoke(sessionDir);
            return Task.FromResult(_result);
        }
    }

    private static ProcessRunResult Success(string stdout) => new(0, stdout, "", false);

    private static string Observation(string screenshot = "", int elementCount = 0)
    {
        var els = string.Join(",", Enumerable.Range(1, elementCount)
            .Select(i => $"{{\"ref\":{i},\"role\":\"link\",\"name\":\"item{i}\",\"value\":null}}"));
        var shot = string.IsNullOrEmpty(screenshot) ? "null" : $"\"{screenshot}\"";
        return $"{{\"url\":\"http://1.1.1.1/\",\"title\":\"Example\",\"elements\":[{els}],\"screenshot\":{shot},\"error\":null}}";
    }

    private static ComputerUseOptions EnabledOptions(Action<ComputerUseOptions>? tweak = null)
    {
        var options = new ComputerUseOptions
        {
            Enabled = true,
            Image = "computer-use/browser:latest",
            RuntimePath = "docker",
            StepTimeoutSeconds = 30,
            MemoryMb = 512,
            Cpus = 1.0,
            PidsLimit = 512,
            MaxScreenshotBytes = 5 * 1024 * 1024,
            MaxElements = 100,
            AllowedHosts = new List<string> { "1.1.1.1" },
        };
        tweak?.Invoke(options);
        return options;
    }

    private static UserResourceAccessService Resources() =>
        new(new ToolSandboxService(NullLogger<ToolSandboxService>.Instance));

    private static ComputerUseExecutor CreateExecutor(ComputerUseOptions options, IProcessRunner runner) =>
        new(Options.Create(options), runner, new ToolSandboxService(NullLogger<ToolSandboxService>.Instance),
            Resources(), NullLogger<ComputerUseExecutor>.Instance);

    private static string NewSessionDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cu-test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string UploadDir() =>
        Path.Combine(Directory.GetCurrentDirectory(), "Uploads", TenantId.ToString("N"), UserId.ToString("N"));

    private static void Cleanup(string sessionDir)
    {
        try { if (Directory.Exists(sessionDir)) Directory.Delete(sessionDir, true); } catch { }
        try { var u = UploadDir(); if (Directory.Exists(u)) Directory.Delete(u, true); } catch { }
    }

    private static ComputerUseAction Navigate(string url) => new() { Type = ComputerUseActionType.Navigate, Url = url };
    private static ComputerUseAction Click(int r) => new() { Type = ComputerUseActionType.Click, Ref = r };

    private static void AssertFlagWithValue(IReadOnlyList<string> args, string flag, string value)
    {
        for (var i = 0; i < args.Count - 1; i++)
            if (args[i] == flag && args[i + 1] == value) return;
        Assert.Fail($"Expected '{flag}' immediately followed by '{value}'. Actual: {string.Join(' ', args)}");
    }

    private static string? EnvValue(IReadOnlyList<string> args, string prefix)
    {
        for (var i = 0; i < args.Count - 1; i++)
            if (args[i] == "--env" && args[i + 1].StartsWith(prefix, StringComparison.Ordinal))
                return args[i + 1];
        return null;
    }

    private static string? ExtractSessionDir(IReadOnlyList<string> args)
    {
        const string suffix = ":/session:rw";
        for (var i = 0; i < args.Count - 1; i++)
            if (args[i] == "--volume" && args[i + 1].EndsWith(suffix, StringComparison.Ordinal))
                return args[i + 1][..^suffix.Length];
        return null;
    }

    // ── Enable gate ──

    [Fact]
    public async Task Disabled_ReturnsNotConfigured_AndNeverCallsRunner()
    {
        var runner = new FakeProcessRunner(Success(Observation()));
        var executor = CreateExecutor(EnabledOptions(o => o.Enabled = false), runner);
        var session = NewSessionDir();
        try
        {
            var obs = await executor.StepAsync(Navigate(PublicUrl), TenantId, UserId, session);
            Assert.True(obs.IsError);
            Assert.Contains("chưa được cấu hình", obs.Error);
            Assert.False(executor.IsEnabled);
            Assert.Equal(0, runner.CallCount);
        }
        finally { Cleanup(session); }
    }

    [Fact]
    public void IsEnabled_TrueOnlyWhenEnabledAndImageConfigured()
    {
        var runner = new FakeProcessRunner(Success(Observation()));
        Assert.True(CreateExecutor(EnabledOptions(), runner).IsEnabled);
        Assert.False(CreateExecutor(EnabledOptions(o => o.Enabled = false), runner).IsEnabled);
        Assert.False(CreateExecutor(EnabledOptions(o => o.Image = "  "), runner).IsEnabled);
    }

    // ── Hardened docker arguments + net-allowlist ──

    [Fact]
    public async Task Navigate_HappyPath_PassesHardenedArguments_AndParsesObservation()
    {
        var runner = new FakeProcessRunner(Success(Observation(elementCount: 2)));
        var options = EnabledOptions();
        var executor = CreateExecutor(options, runner);
        var session = NewSessionDir();
        try
        {
            var obs = await executor.StepAsync(Navigate(PublicUrl), TenantId, UserId, session);

            Assert.False(obs.IsError);
            Assert.Equal("Example", obs.Title);
            Assert.Equal(2, obs.Elements.Count);
            Assert.Equal(1, runner.CallCount);
            Assert.Equal("docker", runner.FileName);

            var args = runner.Arguments!;
            Assert.Equal("run", args[0]);
            Assert.Contains("--rm", args);
            Assert.Contains("--read-only", args);
            Assert.Contains("--pids-limit", args);
            Assert.Contains("computer-use/browser:latest", args);
            AssertFlagWithValue(args, "--cap-drop", "ALL");
            AssertFlagWithValue(args, "--user", "65534:65534");
            AssertFlagWithValue(args, "--memory", "512m");
            AssertFlagWithValue(args, "--memory-swap", "512m");   // == memory ⇒ swap off
            AssertFlagWithValue(args, "--security-opt", "no-new-privileges");

            // The action JSON is the trailing positional argument after --action.
            AssertFlagWithValue(args, "--action", args[^1]);
            Assert.Contains("navigate", args[^1]);

            // Timeout = configured step budget + 5s spin-up grace.
            Assert.Equal(TimeSpan.FromSeconds(options.StepTimeoutSeconds) + TimeSpan.FromSeconds(5), runner.Timeout);
        }
        finally { Cleanup(session); }
    }

    [Fact]
    public async Task Navigate_PropagatesAllowlist_AndPinsNetwork_WhenConfigured()
    {
        var runner = new FakeProcessRunner(Success(Observation()));
        var options = EnabledOptions(o =>
        {
            o.NetworkName = "cu-egress";
            o.AllowedHosts = new List<string> { "1.1.1.1", "example.com" };
        });
        var executor = CreateExecutor(options, runner);
        var session = NewSessionDir();
        try
        {
            await executor.StepAsync(Navigate(PublicUrl), TenantId, UserId, session);

            var args = runner.Arguments!;
            AssertFlagWithValue(args, "--network", "cu-egress");                  // pinned egress network
            var env = EnvValue(args, "COMPUTER_USE_ALLOWED_HOSTS=");
            Assert.NotNull(env);                                                  // allowlist propagated to the image
            Assert.Contains("1.1.1.1", env);
            Assert.Contains("example.com", env);
        }
        finally { Cleanup(session); }
    }

    [Fact]
    public async Task Navigate_NoNetworkFlag_WhenNetworkNameUnset()
    {
        var runner = new FakeProcessRunner(Success(Observation()));
        var executor = CreateExecutor(EnabledOptions(), runner); // NetworkName empty by default
        var session = NewSessionDir();
        try
        {
            await executor.StepAsync(Navigate(PublicUrl), TenantId, UserId, session);
            Assert.DoesNotContain("--network", runner.Arguments!);
        }
        finally { Cleanup(session); }
    }

    // ── SSRF gate — refuses internal targets BEFORE any launch ──

    [Theory]
    [InlineData("http://127.0.0.1/admin")]
    [InlineData("http://169.254.169.254/latest/meta-data")]
    [InlineData("http://localhost:8080/")]
    [InlineData("file:///etc/passwd")]
    public async Task Navigate_SsrfTarget_IsRefused_BeforeAnyLaunch(string url)
    {
        var runner = new FakeProcessRunner(Success(Observation()));
        // Allow the host in the allowlist to prove the SSRF gate is what refuses it.
        var host = Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : url;
        var executor = CreateExecutor(EnabledOptions(o => o.AllowedHosts = new List<string> { host }), runner);
        var session = NewSessionDir();
        try
        {
            var obs = await executor.StepAsync(Navigate(url), TenantId, UserId, session);
            Assert.True(obs.IsError);
            Assert.Equal(0, runner.CallCount);   // refused before the container launched
        }
        finally { Cleanup(session); }
    }

    // ── Strict deny-all allowlist ──

    [Fact]
    public async Task Navigate_EmptyAllowlist_DeniesEverything_BeforeLaunch()
    {
        var runner = new FakeProcessRunner(Success(Observation()));
        var executor = CreateExecutor(EnabledOptions(o => o.AllowedHosts = new List<string>()), runner);
        var session = NewSessionDir();
        try
        {
            var obs = await executor.StepAsync(Navigate(PublicUrl), TenantId, UserId, session);
            Assert.True(obs.IsError);
            Assert.Contains("không nằm trong danh sách", obs.Error);
            Assert.Equal(0, runner.CallCount);
        }
        finally { Cleanup(session); }
    }

    [Fact]
    public async Task Navigate_HostNotOnAllowlist_IsRefused_BeforeLaunch()
    {
        var runner = new FakeProcessRunner(Success(Observation()));
        var executor = CreateExecutor(EnabledOptions(o => o.AllowedHosts = new List<string> { "example.com" }), runner);
        var session = NewSessionDir();
        try
        {
            var obs = await executor.StepAsync(Navigate(PublicUrl), TenantId, UserId, session); // 1.1.1.1 not listed
            Assert.True(obs.IsError);
            Assert.Equal(0, runner.CallCount);
        }
        finally { Cleanup(session); }
    }

    [Fact]
    public async Task NonNavigateAction_DoesNotTriggerAllowlistOrSsrf_AndRuns()
    {
        // A click carries no URL, so it is never allowlist/SSRF gated — it just runs.
        var runner = new FakeProcessRunner(Success(Observation(elementCount: 1)));
        var executor = CreateExecutor(EnabledOptions(o => o.AllowedHosts = new List<string>()), runner);
        var session = NewSessionDir();
        try
        {
            var obs = await executor.StepAsync(Click(1), TenantId, UserId, session);
            Assert.False(obs.IsError);
            Assert.Equal(1, runner.CallCount);
            Assert.Contains("click", runner.Arguments![^1]);
        }
        finally { Cleanup(session); }
    }

    // ── Screenshot harvesting ──

    [Fact]
    public async Task Screenshot_IsHarvested_IntoOwnerUploadRoot()
    {
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3, 4 };
        var runner = new FakeProcessRunner(
            Success(Observation(screenshot: "shot.png")),
            produce: dir => File.WriteAllBytes(Path.Combine(dir, "shot.png"), png));
        var executor = CreateExecutor(EnabledOptions(), runner);
        var session = NewSessionDir();
        try
        {
            var obs = await executor.StepAsync(Click(1), TenantId, UserId, session);

            Assert.NotNull(obs.ScreenshotFileId);
            Assert.EndsWith(".png", obs.ScreenshotFileId);
            Assert.NotEqual("shot.png", obs.ScreenshotFileId);  // server-generated name
            var stored = Path.Combine(UploadDir(), obs.ScreenshotFileId!);
            Assert.True(File.Exists(stored));
            Assert.Equal(png, await File.ReadAllBytesAsync(stored));
        }
        finally { Cleanup(session); }
    }

    [Fact]
    public async Task Screenshot_OverByteCap_IsDropped()
    {
        var big = new byte[200];
        var runner = new FakeProcessRunner(
            Success(Observation(screenshot: "shot.png")),
            produce: dir => File.WriteAllBytes(Path.Combine(dir, "shot.png"), big));
        var executor = CreateExecutor(EnabledOptions(o => o.MaxScreenshotBytes = 50), runner);
        var session = NewSessionDir();
        try
        {
            var obs = await executor.StepAsync(Click(1), TenantId, UserId, session);
            Assert.Null(obs.ScreenshotFileId);   // oversized → dropped, rest of observation intact
        }
        finally { Cleanup(session); }
    }

    // ── Failure modes ──

    [Fact]
    public async Task TimedOut_ReturnsErrorObservation()
    {
        var runner = new FakeProcessRunner(new ProcessRunResult(-1, "", "", true));
        var executor = CreateExecutor(EnabledOptions(o => o.StepTimeoutSeconds = 30), runner);
        var session = NewSessionDir();
        try
        {
            var obs = await executor.StepAsync(Click(1), TenantId, UserId, session);
            Assert.True(obs.IsError);
            Assert.Contains("30s", obs.Error);
        }
        finally { Cleanup(session); }
    }

    [Fact]
    public async Task NonZeroExit_ReturnsErrorObservation()
    {
        var runner = new FakeProcessRunner(new ProcessRunResult(1, "", "boom", false));
        var executor = CreateExecutor(EnabledOptions(), runner);
        var session = NewSessionDir();
        try
        {
            var obs = await executor.StepAsync(Click(1), TenantId, UserId, session);
            Assert.True(obs.IsError);
            Assert.Contains("exit 1", obs.Error);
        }
        finally { Cleanup(session); }
    }

    [Fact]
    public async Task MalformedObservation_ReturnsErrorObservation()
    {
        var runner = new FakeProcessRunner(Success("not json at all"));
        var executor = CreateExecutor(EnabledOptions(), runner);
        var session = NewSessionDir();
        try
        {
            var obs = await executor.StepAsync(Click(1), TenantId, UserId, session);
            Assert.True(obs.IsError);
        }
        finally { Cleanup(session); }
    }

    [Fact]
    public async Task Elements_AreCappedByMaxElements()
    {
        var runner = new FakeProcessRunner(Success(Observation(elementCount: 10)));
        var executor = CreateExecutor(EnabledOptions(o => o.MaxElements = 3), runner);
        var session = NewSessionDir();
        try
        {
            var obs = await executor.StepAsync(Click(1), TenantId, UserId, session);
            Assert.Equal(3, obs.Elements.Count);
        }
        finally { Cleanup(session); }
    }
}
