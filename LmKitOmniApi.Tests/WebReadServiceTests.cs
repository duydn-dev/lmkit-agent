using System.Net;
using LMKit.Agents.Tools.BuiltIn.Net;
using LmKitOmniApi.Infrastructure.AI;
using LmKitOmniApi.Infrastructure.AI.Security;
using LmKitOmniApi.Infrastructure.AI.Web;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LmKitOmniApi.Tests;

/// <summary>
/// Unit tests for <see cref="LmKitWebReadService"/> — the native fetch-and-read tool
/// (fetch_web / WEB_FETCH) wrapping LM-Kit.NET's WebReadTool. NO real network: the
/// actual LM-Kit fetch is behind the <see cref="IWebPageReader"/> seam and a fake
/// records exactly whether/how the service invoked it, so we can prove the SSRF gate
/// fires BEFORE any fetch, the enable gating, the length cap and the citation — all
/// hermetically. Real fetching (LmKitWebPageReader → WebReadTool.InvokeAsync) is
/// live-only and is never exercised here.
///
/// Hermetic-DNS note: the real <see cref="ToolSandboxService.ValidateUrlAsync"/> is
/// used (the contract requires calling it). Happy-path/cap tests target a PUBLIC literal
/// IP (1.1.1.1), for which Dns.GetHostAddresses short-circuits and performs no network
/// query; SSRF-refusal tests target loopback/link-local/localhost literals rejected
/// synchronously before any DNS or fetch.
/// </summary>
public class LmKitWebReadServiceTests
{
    private const string PublicUrl = "http://1.1.1.1/";

    private sealed class FakeWebPageReader : IWebPageReader
    {
        private readonly string _toReturn;
        public FakeWebPageReader(string toReturn) => _toReturn = toReturn;

        public int CallCount { get; private set; }
        public string? LastUrl { get; private set; }

        public Task<string> ReadAsync(string url, CancellationToken ct = default)
        {
            CallCount++;
            LastUrl = url;
            return Task.FromResult(_toReturn);
        }
    }

    private static WebReadOptions EnabledOptions(Action<WebReadOptions>? tweak = null)
    {
        var options = new WebReadOptions
        {
            Enabled = true,
            MaxContentChars = 8_000,
            MaxResponseBytes = 5_000_000,
            MaxRedirects = 5,
            TimeoutSeconds = 20,
            UserAgent = "test/1.0",
        };
        tweak?.Invoke(options);
        return options;
    }

    private static LmKitWebReadService CreateService(WebReadOptions options, IWebPageReader reader) =>
        new(Options.Create(options),
            new ToolSandboxService(NullLogger<ToolSandboxService>.Instance),
            reader,
            NullLogger<LmKitWebReadService>.Instance);

    // ── 1. Enable gating — disabled never fetches ──

    [Fact]
    public async Task Disabled_ReturnsNotEnabled_AndNeverReadsPage()
    {
        var reader = new FakeWebPageReader("should never run");
        var service = CreateService(EnabledOptions(o => o.Enabled = false), reader);

        var result = await service.ReadAsync(PublicUrl);

        Assert.Equal("[WebRead] Công cụ đọc web chưa được bật.", result);
        Assert.False(service.IsEnabled);
        Assert.Equal(0, reader.CallCount);
    }

    [Fact]
    public void IsEnabled_ReflectsOptions()
    {
        var reader = new FakeWebPageReader("x");
        Assert.True(CreateService(EnabledOptions(), reader).IsEnabled);
        Assert.False(CreateService(EnabledOptions(o => o.Enabled = false), reader).IsEnabled);
    }

    // ── 2. SSRF gate — refuses internal targets BEFORE any fetch ──

    [Theory]
    [InlineData("http://127.0.0.1/admin")]
    [InlineData("http://169.254.169.254/latest/meta-data")] // cloud metadata endpoint
    [InlineData("http://localhost:8080/")]
    [InlineData("file:///etc/passwd")]                       // unsupported scheme
    public async Task SsrfTarget_IsRefused_BeforeAnyFetch(string url)
    {
        var reader = new FakeWebPageReader("should never run");
        var service = CreateService(EnabledOptions(), reader);

        var result = await service.ReadAsync(url);

        Assert.StartsWith("[WebRead] URL bị từ chối:", result);
        Assert.Equal(0, reader.CallCount); // the SSRF gate fired before any fetch
    }

    [Fact]
    public async Task SsrfTarget_WithExtractionInstruction_IsStillRefused_BeforeAnyFetch()
    {
        // "url|what-to-extract" — the internal URL portion must still be refused.
        var reader = new FakeWebPageReader("should never run");
        var service = CreateService(EnabledOptions(), reader);

        var result = await service.ReadAsync("http://127.0.0.1/secret|hãy tóm tắt trang này");

        Assert.StartsWith("[WebRead] URL bị từ chối:", result);
        Assert.Equal(0, reader.CallCount);
    }

    // ── 3. Empty / over-long URL — never fetches ──

    [Theory]
    [InlineData("   ")]
    [InlineData("|only an instruction, no url")]
    public async Task EmptyUrl_ReturnsFriendlyMessage_AndNeverReads(string query)
    {
        var reader = new FakeWebPageReader("unused");
        var service = CreateService(EnabledOptions(), reader);

        var result = await service.ReadAsync(query);

        Assert.Equal("[WebRead] Không có URL để đọc.", result);
        Assert.Equal(0, reader.CallCount);
    }

    // ── 4. Happy path — citation + only the URL is fetched ──

    [Fact]
    public async Task HappyPath_ReturnsCitationAndContent_AndFetchesOnlyTheUrl()
    {
        var reader = new FakeWebPageReader("# Release notes\n\nBreaking change: X was removed.");
        var service = CreateService(EnabledOptions(), reader);

        var result = await service.ReadAsync($"{PublicUrl}|tóm tắt các thay đổi");

        Assert.Equal(1, reader.CallCount);
        Assert.Equal(PublicUrl, reader.LastUrl);                 // trailing instruction stripped
        Assert.StartsWith($"Nguồn: {PublicUrl}", result);        // citation line
        Assert.Contains("Breaking change: X was removed.", result);
    }

    [Fact]
    public async Task EmptyContent_ReturnsNoContentNotice()
    {
        var reader = new FakeWebPageReader("   ");
        var service = CreateService(EnabledOptions(), reader);

        var result = await service.ReadAsync(PublicUrl);

        Assert.Equal("(không có nội dung)", result);
    }

    // ── 5. Length cap ──

    [Fact]
    public async Task OversizedContent_IsCappedAtMaxContentChars_WithMarker()
    {
        var big = new string('x', 500);
        var reader = new FakeWebPageReader(big);
        var service = CreateService(EnabledOptions(o => o.MaxContentChars = 100), reader);

        var result = await service.ReadAsync(PublicUrl);

        Assert.Contains(new string('x', 100), result);                                      // first 100 kept
        Assert.Contains("[Nội dung đã bị cắt bớt vì vượt quá 100 ký tự]", result);           // marker
        Assert.DoesNotContain(new string('x', 200), result);                                // tail dropped
        Assert.True(result.Length < big.Length + 100, "capped output must be far shorter than the raw body");
        Assert.Equal(1, reader.CallCount);
    }

    // ── 6. Fetch-level failure is swallowed into a safe, bracketed message ──

    [Fact]
    public async Task ReaderThrows_ReturnsSafeBracketedMessage_NotTheRawException()
    {
        var service = new LmKitWebReadService(
            Options.Create(EnabledOptions()),
            new ToolSandboxService(NullLogger<ToolSandboxService>.Instance),
            new ThrowingReader(),
            NullLogger<LmKitWebReadService>.Instance);

        var result = await service.ReadAsync(PublicUrl);

        Assert.StartsWith("[WebRead]", result);
        Assert.DoesNotContain("egress refused: super-secret internal reason", result);
    }

    private sealed class ThrowingReader : IWebPageReader
    {
        public Task<string> ReadAsync(string url, CancellationToken ct = default) =>
            throw new InvalidOperationException("egress refused: super-secret internal reason");
    }
}

/// <summary>
/// RBAC policy for the "FetchWeb" tool: granted to User/Admin (never Guest), read-only
/// egress so — unlike BrowseWeb — NOT approval-required, and rate limited. (Class name
/// contains "FetchWeb" so the chunk's test filter picks it up.)
/// </summary>
public class FetchWebPermissionTests
{
    private static ToolPermissionService NewService() => new(NullLogger<ToolPermissionService>.Instance);

    [Fact]
    public async Task FetchWeb_IsAllowedWithoutApproval_ForUserAndAdmin()
    {
        var permissions = NewService();

        var admin = await permissions.CanInvokeToolAsync(Guid.NewGuid(), Guid.NewGuid(), "Admin", "FetchWeb");
        var user = await permissions.CanInvokeToolAsync(Guid.NewGuid(), Guid.NewGuid(), "User", "FetchWeb");

        Assert.True(admin.IsAllowed);
        Assert.False(admin.RequiresApproval);
        Assert.True(user.IsAllowed);
        Assert.False(user.RequiresApproval);
    }

    [Fact]
    public async Task FetchWeb_IsDenied_ForGuest()
    {
        var permissions = NewService();

        var guest = await permissions.CanInvokeToolAsync(Guid.NewGuid(), Guid.NewGuid(), "Guest", "FetchWeb");

        Assert.False(guest.IsAllowed);
        Assert.False(guest.RequiresApproval);
    }

    [Fact]
    public async Task FetchWeb_IsListedForUserRole()
    {
        var permissions = NewService();

        var allowed = await permissions.GetAllowedToolsAsync("User");

        Assert.Contains("FetchWeb", allowed);
    }

    [Fact]
    public async Task FetchWeb_IsRateLimited()
    {
        var permissions = NewService();
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        for (var i = 0; i < 10; i++)
            await permissions.RecordToolInvocationAsync(tenantId, userId, "FetchWeb");

        var result = await permissions.CanInvokeToolAsync(tenantId, userId, "User", "FetchWeb");

        Assert.False(result.IsAllowed);
        Assert.Contains("Rate limit", result.DenialReason);
    }
}

/// <summary>
/// The WEB_FETCH action must map to the "FetchWeb" permission name so RBAC and the
/// custom-agent whitelist resolve correctly. (Class name contains "WebFetch".)
/// </summary>
public class WebFetchActionMappingTests
{
    [Fact]
    public void WebFetch_MapsTo_FetchWebPermission()
    {
        Assert.True(AgentOrchestrator.ActionToToolMap.TryGetValue("WEB_FETCH", out var mapped));
        Assert.Equal("FetchWeb", mapped);
    }

    [Fact]
    public void WebFetch_Mapping_IsCaseInsensitive()
    {
        Assert.Equal("FetchWeb", AgentOrchestrator.ActionToToolMap["web_fetch"]);
    }
}

/// <summary>
/// The REAL LM-Kit.NET <see cref="WebEgressPolicy"/> gate that WebReadTool fetches
/// through must, by construction, refuse the host's own network. Exercised directly on
/// literal internal addresses — no DNS, no network, no model — as the second, native
/// layer of SSRF defense behind the service's pre-flight check. (Class name contains
/// "WebFetch".)
/// </summary>
public class WebFetchEgressPolicyTests
{
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("169.254.169.254")] // link-local — cloud metadata lives here
    [InlineData("10.0.0.1")]
    [InlineData("192.168.1.1")]
    [InlineData("172.16.0.1")]
    public void IsPublicAddress_IsFalse_ForInternalRanges(string ip)
    {
        Assert.False(WebEgressPolicy.IsPublicAddress(IPAddress.Parse(ip)));
    }

    [Theory]
    [InlineData("1.1.1.1")]
    [InlineData("93.184.216.34")]
    public void IsPublicAddress_IsTrue_ForPublicAddresses(string ip)
    {
        Assert.True(WebEgressPolicy.IsPublicAddress(IPAddress.Parse(ip)));
    }

    [Theory]
    [InlineData("http://127.0.0.1/")]
    [InlineData("http://169.254.169.254/latest/meta-data")]
    public void ValidateRequest_Refuses_InternalLiteralHosts(string url)
    {
        // PublicWeb by default; a hermetic identity resolver keeps this offline even if
        // the gate resolves the literal host (both inputs are literal IPs).
        var policy = new WebEgressPolicy
        {
            Resolve = host => new[] { IPAddress.Parse(host) },
        };

        var decision = policy.ValidateRequest(new Uri(url));

        Assert.False(decision.Allowed);
        Assert.False(string.IsNullOrWhiteSpace(decision.Reason));
    }
}
