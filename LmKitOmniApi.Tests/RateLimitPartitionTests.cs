using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using LmKitOmniApi.Infrastructure.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;

namespace LmKitOmniApi.Tests;

/// <summary>
/// One host, shared by both integration tests below, configured with a 2-request
/// budget in a one-hour window so a partition is exhausted in three calls. Every test
/// uses a DIFFERENT caller identity, so they occupy different partitions and are
/// order-independent.
/// </summary>
public sealed class RateLimitPartitionFixture : IAsyncLifetime
{
    public LmKitApiFactory Parent { get; } = new();
    public WebApplicationFactory<Program> Host { get; private set; } = null!;

    /// <summary>Cookie-JWT client for the seeded user. One login for the whole class.</summary>
    public HttpClient JwtClient { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        Parent.EnsureSeeded();
        Host = RateLimitTestHost.Create(Parent, new Dictionary<string, string?>
        {
            ["RateLimiting:AiRequestsPerWindow"] = "2",
            ["RateLimiting:AiWindowSeconds"] = "3600"
        });

        JwtClient = Host.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true
        });
        var login = await JwtClient.PostAsJsonAsync("/api/auth/login", new
        {
            email = LmKitApiFactory.Email,
            password = LmKitApiFactory.Password
        });
        if (login.StatusCode != HttpStatusCode.OK)
            throw new InvalidOperationException($"Fixture login failed: {login.StatusCode}");
    }

    public Task DisposeAsync()
    {
        JwtClient.Dispose();
        Host.Dispose();
        Parent.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>Mints a key for the seeded user and returns a client presenting it.</summary>
    public async Task<HttpClient> CreateApiKeyClientAsync(string name)
    {
        var response = await JwtClient.PostAsJsonAsync("/api/api-keys", new { name });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var rawKey = body.GetProperty("rawKey").GetString();
        Assert.False(string.IsNullOrWhiteSpace(rawKey));

        var client = Host.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", rawKey);
        return client;
    }
}

/// <summary>
/// The product requirement, exercised through the real pipeline: "rate-limit theo
/// apiKey, ClientId trong jwt, nếu mấy cái đó ko có mới check theo ip" — partition by
/// API key first, then by the JWT identity, and only fall back to IP.
/// </summary>
public sealed class RateLimitPartitionTests : IClassFixture<RateLimitPartitionFixture>
{
    private readonly RateLimitPartitionFixture _fixture;

    public RateLimitPartitionTests(RateLimitPartitionFixture fixture)
    {
        _fixture = fixture;
    }

    /// <summary>
    /// HEADLINE REGRESSION. Both policies used to partition on the caller's USER id
    /// (<c>User.Identity.Name</c>), so every API key a user owned drew on ONE budget:
    /// a single busy integration throttled all the others. Before the fix the second
    /// key's very first call was already the third request in the shared bucket and
    /// came back 429; it must now be permitted.
    /// </summary>
    [Fact]
    public async Task TwoApiKeysOfTheSameUser_GetIndependentBudgets()
    {
        using var firstKeyClient = await _fixture.CreateApiKeyClientAsync("partition-key-a");
        using var secondKeyClient = await _fixture.CreateApiKeyClientAsync("partition-key-b");

        var firstKeyStatuses = new List<HttpStatusCode>();
        for (var attempt = 0; attempt < 3; attempt++)
            firstKeyStatuses.Add(await RateLimitTestHost.AiRequestAsync(firstKeyClient));

        var secondKeyFirstCall = await RateLimitTestHost.AiRequestAsync(secondKeyClient);
        var secondKeyExhausted = new List<HttpStatusCode>();
        for (var attempt = 0; attempt < 2; attempt++)
            secondKeyExhausted.Add(await RateLimitTestHost.AiRequestAsync(secondKeyClient));

        // Key A: two permitted (400 = model-free validation), the third throttled.
        Assert.Equal(
            [HttpStatusCode.BadRequest, HttpStatusCode.BadRequest, HttpStatusCode.TooManyRequests],
            firstKeyStatuses);
        // Key B owns a SEPARATE budget: unaffected by key A being exhausted.
        Assert.Equal(HttpStatusCode.BadRequest, secondKeyFirstCall);
        // ...and that budget really is a budget, not an exemption.
        Assert.Equal(
            [HttpStatusCode.BadRequest, HttpStatusCode.TooManyRequests],
            secondKeyExhausted);
    }

    /// <summary>
    /// An API-key caller and the cookie-JWT session of the SAME user must not share a
    /// bucket either: the key is the budget holder, the browser session is its own.
    /// Before the fix both collapsed onto the user id.
    /// </summary>
    [Fact]
    public async Task JwtSessionAndApiKeyOfTheSameUser_DoNotShareOneBudget()
    {
        using var keyClient = await _fixture.CreateApiKeyClientAsync("partition-key-c");

        var jwtStatuses = new List<HttpStatusCode>();
        for (var attempt = 0; attempt < 3; attempt++)
            jwtStatuses.Add(await RateLimitTestHost.AiRequestAsync(_fixture.JwtClient));

        var keyFirstCall = await RateLimitTestHost.AiRequestAsync(keyClient);

        Assert.Equal(
            [HttpStatusCode.BadRequest, HttpStatusCode.BadRequest, HttpStatusCode.TooManyRequests],
            jwtStatuses);
        Assert.Equal(HttpStatusCode.BadRequest, keyFirstCall);
    }
}

/// <summary>
/// Focused checks on the derivation itself. These are supporting evidence, not the
/// proof — the pipeline tests above are the proof. Their value is covering the two
/// branches the integration tests cannot reach: the IP fallback for a caller that got
/// far enough to be limited while anonymous (every <c>ai-agent</c> endpoint is
/// <c>[Authorize]</c>, so an anonymous request is rejected before the limiter runs),
/// and the no-remote-IP case.
/// </summary>
public sealed class RateLimitPartitionKeyTests
{
    private static readonly Guid TenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid UserId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid ApiKeyId = Guid.Parse("99999999-9999-9999-9999-999999999999");

    [Fact]
    public void ApiKeyIdentity_TakesPrecedenceOverTheOwningUser()
    {
        var context = Authenticated(
            new Claim(ClaimTypes.NameIdentifier, UserId.ToString()),
            new Claim("TenantId", TenantId.ToString()),
            new Claim(ApiKeyAuthenticationHandler.ApiKeyIdClaimType, ApiKeyId.ToString()));
        context.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.9");

        Assert.Equal($"apikey:{ApiKeyId}", RateLimitPartitionKey.Resolve(context));
    }

    [Fact]
    public void JwtIdentity_IsPartitionedByTenantAndSubject()
    {
        var context = Authenticated(
            new Claim(ClaimTypes.NameIdentifier, UserId.ToString()),
            new Claim("TenantId", TenantId.ToString()));
        context.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.9");

        Assert.Equal($"user:{TenantId}:{UserId}", RateLimitPartitionKey.Resolve(context));
    }

    [Fact]
    public void AnonymousCaller_FallsBackToTheClientIp()
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.9");

        var partition = RateLimitPartitionKey.Resolve(context);

        Assert.Equal("ip:203.0.113.9", partition);
        Assert.True(RateLimitPartitionKey.IsIpPartition(partition));
    }

    [Fact]
    public void AnonymousCallerWithoutARemoteAddress_LandsInTheSharedAnonymousBucket()
    {
        var partition = RateLimitPartitionKey.Resolve(new DefaultHttpContext());

        Assert.Equal(RateLimitPartitionKey.AnonymousPartition, partition);
        Assert.False(RateLimitPartitionKey.IsIpPartition(partition));
    }

    /// <summary>
    /// The same host reached as an IPv4-mapped IPv6 address must land in ONE bucket,
    /// otherwise anyone able to influence which form the proxy emits doubles their budget.
    /// </summary>
    [Fact]
    public void IPv4MappedAddresses_ShareOneBucketWithTheirIPv4Form()
    {
        var mapped = new DefaultHttpContext();
        mapped.Connection.RemoteIpAddress = IPAddress.Parse("::ffff:203.0.113.9");
        var plain = new DefaultHttpContext();
        plain.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.9");

        Assert.Equal(
            RateLimitPartitionKey.Resolve(plain),
            RateLimitPartitionKey.Resolve(mapped));
    }

    /// <summary>
    /// Every branch is prefixed, so an address can never be mistaken for an identity.
    /// Without the prefixes a bare "203.0.113.9" and a bare user id shared a namespace.
    /// </summary>
    [Fact]
    public void PartitionsFromDifferentBranches_NeverCollide()
    {
        var apiKey = Authenticated(
            new Claim(ClaimTypes.NameIdentifier, UserId.ToString()),
            new Claim(ApiKeyAuthenticationHandler.ApiKeyIdClaimType, ApiKeyId.ToString()));
        var jwt = Authenticated(new Claim(ClaimTypes.NameIdentifier, UserId.ToString()));
        var anonymous = new DefaultHttpContext();
        anonymous.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.9");

        var partitions = new[]
        {
            RateLimitPartitionKey.Resolve(apiKey),
            RateLimitPartitionKey.Resolve(jwt),
            RateLimitPartitionKey.Resolve(anonymous),
            RateLimitPartitionKey.Resolve(new DefaultHttpContext())
        };

        Assert.Equal(partitions.Length, partitions.Distinct(StringComparer.Ordinal).Count());
        Assert.All(partitions, partition => Assert.Contains(':', partition));
    }

    /// <summary>
    /// The pure-IP policies (login, share links, widget key exchange) must stay pure-IP
    /// even when the caller is somehow authenticated, or an attacker could mint a fresh
    /// brute-force budget per identity they present.
    /// </summary>
    [Fact]
    public void ResolveClientIp_IgnoresTheAuthenticatedIdentityEntirely()
    {
        var context = Authenticated(
            new Claim(ClaimTypes.NameIdentifier, UserId.ToString()),
            new Claim(ApiKeyAuthenticationHandler.ApiKeyIdClaimType, ApiKeyId.ToString()));
        context.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.9");

        Assert.Equal("ip:203.0.113.9", RateLimitPartitionKey.ResolveClientIp(context));
    }

    private static DefaultHttpContext Authenticated(params Claim[] claims)
        => new()
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "TestScheme"))
        };
}
