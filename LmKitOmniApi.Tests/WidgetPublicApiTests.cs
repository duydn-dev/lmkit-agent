using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LmKitOmniApi.Application.Widget;
using LmKitOmniApi.Domain.Entities;
using LmKitOmniApi.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using LmKitOmniApi.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.DependencyInjection;

namespace LmKitOmniApi.Tests;

/// <summary>
/// Integration coverage for the PUBLIC widget surface: key exchange, origin
/// allowlist enforcement, widget-token chat, rotation, quota, throttling and
/// cross-tenant isolation. Only the LM boundary is canned
/// (<see cref="TestWidgetInferenceSessionFactory"/>) so no model load is needed —
/// the chat engine itself and everything around it (auth, policy, quota,
/// controller wiring) is the real production pipeline over SQLite.
/// </summary>
public sealed class WidgetPublicApiTests : IClassFixture<WidgetApiFixture>
{
    private readonly WidgetApiFixture _fixture;

    public WidgetPublicApiTests(WidgetApiFixture fixture)
    {
        _fixture = fixture;
        _fixture.ResetWidgetState();
    }

    [Fact]
    public async Task Exchange_WithoutKey_Returns401()
    {
        var client = _fixture.CreateWidgetClient("https://shop.example.com");
        var response = await client.PostAsJsonAsync("/api/widget/auth", new { origin = "https://shop.example.com" });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Exchange_WithWrongKey_Returns401_IndistinguishableFromDisabled()
    {
        var response = await ExchangeAsync("https://shop.example.com", "bogus-key-that-nobody-issued-aaaaaaaaaaaaa");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Exchange_WithDisallowedOrigin_Returns403()
    {
        var key = await _fixture.SeedActiveWidgetAsync(allowedOrigins: ["https://shop.example.com"]);
        var response = await ExchangeAsync("https://evil.example.com", key);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Exchange_WithValidKeyAndOrigin_ReturnsTokenAndBranding()
    {
        var key = await _fixture.SeedActiveWidgetAsync(
            allowedOrigins: ["https://shop.example.com"],
            title: "Trợ lý cửa hàng",
            brandColor: "#00ff00",
            welcome: "Chào bạn!");

        var client = _fixture.CreateWidgetClient("https://shop.example.com");
        var response = await PostWithHeadersAsync(client, "/api/widget/auth", new { origin = "https://shop.example.com" }, widgetKey: key);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(string.IsNullOrEmpty(body.GetProperty("accessToken").GetString()));
        Assert.Equal("Trợ lý cửa hàng", body.GetProperty("widget").GetProperty("widgetTitle").GetString());
        Assert.Equal("#00ff00", body.GetProperty("widget").GetProperty("brandColor").GetString());

        // The minted token must NOT authenticate any non-widget surface.
        var token = body.GetProperty("accessToken").GetString()!;
        var intruder = _fixture.Factory.CreateClient();
        intruder.DefaultRequestHeaders.Add("X-Widget-Token", token);
        var me = await intruder.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, me.StatusCode);
    }

    [Fact]
    public async Task Exchange_WhenWidgetDisabled_Returns401()
    {
        await _fixture.SeedWidgetAsync(isActive: false, allowedOrigins: ["https://shop.example.com"], issueKey: true);
        var settings = await _fixture.GetSettingsAsync();
        var rawKey = "disabled-widget-key-0000000000000000000";
        await _fixture.SetKeyHashAsync(WidgetSecrets.Hash(rawKey));

        var response = await ExchangeAsync("https://shop.example.com", rawKey);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Chat_WithValidTokenAndOrigin_StreamsGuardrailedAnswer()
    {
        var key = await _fixture.SeedActiveWidgetAsync(allowedOrigins: ["https://shop.example.com"]);
        var token = await ExchangeForTokenAsync(key, "https://shop.example.com");

        var client = _fixture.CreateWidgetClient("https://shop.example.com");
        var response = await PostWithHeadersAsync(client, "/api/widget/chat", new
        {
            message = "Xin chào cửa hàng!",
            history = new[] { new { role = "assistant", content = "Chào bạn!" } }
        }, widgetToken: token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var text = await response.Content.ReadAsStringAsync();
        Assert.Contains("data: ", text);
        Assert.Contains("\"answer\"", text);
        // The REAL WidgetChatEngine ran: these tokens only reach the response if the
        // engine subscribed the model's text-completion event and drained its channel.
        // If that plumbing breaks the guardrail sees an empty answer and the endpoint
        // emits the canned apology instead.
        Assert.Contains(TestWidgetInferenceSessionFactory.CannedAnswer, text);
        Assert.DoesNotContain(WidgetChatEngine.FallbackAnswer, text);
    }

    [Fact]
    public async Task Chat_WithoutToken_Returns401()
    {
        await _fixture.SeedActiveWidgetAsync(allowedOrigins: ["https://shop.example.com"]);
        var client = _fixture.CreateWidgetClient("https://shop.example.com");
        var response = await client.PostAsync("/api/widget/chat", Json(new { message = "hi" }));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Chat_WithWrongOrigin_Returns403()
    {
        var key = await _fixture.SeedActiveWidgetAsync(allowedOrigins: ["https://shop.example.com"]);
        var token = await ExchangeForTokenAsync(key, "https://shop.example.com");

        var client = _fixture.CreateWidgetClient("https://evil.example.com");
        var response = await PostWithHeadersAsync(client, "/api/widget/chat", new { message = "hi" }, widgetToken: token);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Chat_WithOversizedMessage_Returns400()
    {
        var key = await _fixture.SeedActiveWidgetAsync(allowedOrigins: ["https://shop.example.com"]);
        var token = await ExchangeForTokenAsync(key, "https://shop.example.com");
        var client = _fixture.CreateWidgetClient("https://shop.example.com");
        var response = await PostWithHeadersAsync(client, "/api/widget/chat", new { message = new string('x', 2001) }, widgetToken: token);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Rotation_InvalidatesOldKeyImmediately()
    {
        var key = await _fixture.SeedActiveWidgetAsync(allowedOrigins: ["https://shop.example.com"]);

        // Old key works.
        var before = await ExchangeAsync("https://shop.example.com", key);
        Assert.Equal(HttpStatusCode.OK, before.StatusCode);

        // Rotate via the admin API (real MediatR path).
        var newKey = await _fixture.RotateViaAdminApiAsync();

        // Old key is now dead.
        var after = await ExchangeAsync("https://shop.example.com", key);
        Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);

        // New key works.
        var fresh = await ExchangeAsync("https://shop.example.com", newKey);
        Assert.Equal(HttpStatusCode.OK, fresh.StatusCode);
    }

    [Fact]
    public async Task AdminSettings_RequiresAuth_Returns401ForAnonymous()
    {
        var client = _fixture.Factory.CreateClient();
        var response = await client.GetAsync("/api/admin/widget/settings");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task OriginHeader_TakesPrecedenceOverReferer()
    {
        var key = await _fixture.SeedActiveWidgetAsync(allowedOrigins: ["https://shop.example.com"]);
        var client = _fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Widget-Origin", "https://shop.example.com");
        client.DefaultRequestHeaders.Referrer = new Uri("https://evil.example.com/page");

        var response = await PostWithHeadersAsync(client, "/api/widget/auth", new { origin = "ignored" }, widgetKey: key);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Exchange_IsThrottledPerClientAddress()
    {
        // Own host: the shared fixture runs a deliberately generous window so the rest
        // of the suite never trips it. POST /api/widget/auth is anonymous and drives a
        // key-hash lookup, so it must be limited like /api/auth/login and the share
        // links are (per-IP fixed window).
        using var factory = new LmKitApiFactory();
        factory.ConfigurationOverrides["RateLimiting:WidgetAuthRequestsPerWindow"] = "3";
        factory.ConfigurationOverrides["RateLimiting:WidgetAuthWindowSeconds"] = "3600";
        factory.EnsureSeeded();

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HermesDbContext>();
            db.TenantWidgetSettings.Add(new TenantWidgetSettings
            {
                TenantId = LmKitApiFactory.TenantId,
                IsActive = true,
                AllowedOriginsJson = WidgetOrigins.Serialize([WidgetApiFixture.DefaultOrigin])
            });
            await db.SaveChangesAsync();
        }

        HttpResponseMessage? last = null;
        for (var attempt = 1; attempt <= 4; attempt++)
        {
            using var client = factory.CreateClient();
            client.DefaultRequestHeaders.Add("X-Widget-Origin", WidgetApiFixture.DefaultOrigin);
            last?.Dispose();
            last = await client.PostAsync("/api/widget/auth", Json(new { origin = WidgetApiFixture.DefaultOrigin }));
            // The un-keyed request is rejected before any lookup; what matters is that
            // the limiter counts it and cuts the client off on the fourth attempt.
            if (attempt < 4) Assert.Equal(HttpStatusCode.Unauthorized, last.StatusCode);
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, last!.StatusCode);
        Assert.True(last.Headers.TryGetValues("Retry-After", out _));
        last.Dispose();
    }

    [Fact]
    public async Task WidgetSettings_AreUniquePerTenant()
    {
        await _fixture.SeedActiveWidgetAsync(allowedOrigins: [WidgetApiFixture.DefaultOrigin]);

        using var scope = _fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HermesDbContext>();
        db.TenantWidgetSettings.Add(new TenantWidgetSettings { TenantId = LmKitApiFactory.TenantId });

        // Without this index two concurrent PUTs leave two rows behind and EVERY later
        // SingleOrDefaultAsync read throws — the tenant's widget is bricked for good.
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task UpdateSettings_LosingTheInsertRace_StillLeavesExactlyOneRow()
    {
        var connection = _fixture.Factory.Services.GetRequiredService<SqliteConnection>();

        // A competing PUT inserts the tenant's row after this handler's read found
        // nothing but before its INSERT hits the database — the exact interleaving
        // that used to create a second row.
        var interceptor = new InsertRaceInterceptor(async () =>
        {
            await using var competitor = new HermesDbContext(
                new DbContextOptionsBuilder<HermesDbContext>().UseSqlite(connection).Options);
            competitor.TenantWidgetSettings.Add(new TenantWidgetSettings
            {
                TenantId = LmKitApiFactory.TenantId,
                IsActive = false,
                AllowedOriginsJson = WidgetOrigins.Serialize(["https://loser.example.com"])
            });
            await competitor.SaveChangesAsync();
        });

        await using var racing = new HermesDbContext(new DbContextOptionsBuilder<HermesDbContext>()
            .UseSqlite(connection)
            .AddInterceptors(interceptor)
            .Options);

        var result = await new UpdateWidgetSettingsCommandHandler(racing).Handle(
            new UpdateWidgetSettingsCommand
            {
                TenantId = LmKitApiFactory.TenantId,
                IsActive = true,
                AllowedOrigins = [WidgetApiFixture.DefaultOrigin]
            },
            CancellationToken.None);

        Assert.Equal(WidgetMutationStatus.Success, result.Status);

        using var scope = _fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HermesDbContext>();
        var rows = await db.TenantWidgetSettings.AsNoTracking()
            .Where(s => s.TenantId == LmKitApiFactory.TenantId)
            .ToListAsync();

        var surviving = Assert.Single(rows);
        Assert.True(surviving.IsActive);
        Assert.Equal([WidgetApiFixture.DefaultOrigin], WidgetOrigins.Parse(surviving.AllowedOriginsJson));
    }

    [Fact]
    public async Task Chat_BeyondThePerMinuteQuota_Returns429()
    {
        // Both budgets are pinned at 2 so the assertion holds even if the run happens
        // to straddle a minute boundary.
        var key = await _fixture.SeedActiveWidgetAsync(
            allowedOrigins: [WidgetApiFixture.DefaultOrigin], requestsPerMinute: 2, requestsPerDay: 2);
        var token = await ExchangeForTokenAsync(key, WidgetApiFixture.DefaultOrigin);

        var statuses = new List<HttpStatusCode>();
        for (var turn = 0; turn < 3; turn++)
        {
            var client = _fixture.CreateWidgetClient(WidgetApiFixture.DefaultOrigin);
            using var response = await PostWithHeadersAsync(
                client, "/api/widget/chat", new { message = "xin chào" }, widgetToken: token);
            statuses.Add(response.StatusCode);
        }

        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.TooManyRequests], statuses);
    }

    /// <summary>
    /// The public widget under load. Its LM boundary takes the single chat permit inside
    /// <c>OpenAsync</c>, so a refusal surfaces BEFORE the SSE response starts -- and it used to
    /// reach the global handler as an unknown exception: a 500 "unexpected error", logged as a
    /// crash, for what was really "the model is busy". Now a 503 with Retry-After and the
    /// queue's own message, the same shape as the 429 this endpoint already answers for quota.
    /// </summary>
    [Fact]
    public async Task Chat_WhenTheInferenceQueueRefuses_Is503WithRetryAfter_NotA500()
    {
        var key = await _fixture.SeedActiveWidgetAsync(allowedOrigins: [WidgetApiFixture.DefaultOrigin]);

        // A derived host whose LM boundary refuses admission. Both the key exchange and the
        // chat go through it, so its data-protection ring signs and validates the same token.
        using var refusing = _fixture.Factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IWidgetInferenceSessionFactory>();
                services.AddSingleton<IWidgetInferenceSessionFactory>(new RefusingSessionFactory());
            }));

        var client = refusing.CreateClient();
        client.DefaultRequestHeaders.Add("X-Widget-Origin", WidgetApiFixture.DefaultOrigin);
        var exchange = await PostWithHeadersAsync(
            client, "/api/widget/auth", new { origin = WidgetApiFixture.DefaultOrigin }, widgetKey: key);
        exchange.EnsureSuccessStatusCode();
        var token = (await exchange.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString()!;

        var chat = refusing.CreateClient();
        chat.DefaultRequestHeaders.Add("X-Widget-Origin", WidgetApiFixture.DefaultOrigin);
        using var response = await PostWithHeadersAsync(chat, "/api/widget/chat", new { message = "xin chao" }, widgetToken: token);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.NotNull(response.Headers.RetryAfter);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(RefusingSessionFactory.Message, problem.GetProperty("detail").GetString());
    }

    private sealed class RefusingSessionFactory : IWidgetInferenceSessionFactory
    {
        public const string Message = "He thong dang ban, vui long thu lai sau.";

        public ValueTask<IWidgetInferenceSession> OpenAsync(WidgetTurnRequest request, CancellationToken ct)
            => throw new InferenceQueueRejectedException(
                "chat", InferenceQueueRejectionReason.WaitTimeout, TimeSpan.FromSeconds(300), 3, Message);
    }

    // ── helpers ─────────────────────────────────────────────────────────

    /// <summary>Runs an action once, immediately before the intercepted context saves.</summary>
    private sealed class InsertRaceInterceptor(Func<Task> beforeFirstSave) : SaveChangesInterceptor
    {
        private bool _fired;

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (!_fired)
            {
                _fired = true;
                await beforeFirstSave();
            }
            return result;
        }
    }

    private static StringContent Json(object payload) =>
        new(JsonSerializer.Serialize(payload), System.Text.Encoding.UTF8, "application/json");

    private static Task<HttpResponseMessage> PostWithHeadersAsync(
        HttpClient client, string url, object payload,
        string? widgetKey = null, string? widgetToken = null)
    {
        if (widgetKey is not null) client.DefaultRequestHeaders.Add("X-Widget-Key", widgetKey);
        if (widgetToken is not null) client.DefaultRequestHeaders.Add("X-Widget-Token", widgetToken);
        return client.PostAsync(url, Json(payload));
    }

    private Task<HttpResponseMessage> ExchangeAsync(string origin, string key)
    {
        var client = _fixture.CreateWidgetClient(origin);
        return PostWithHeadersAsync(client, "/api/widget/auth", new { origin }, widgetKey: key);
    }

    private async Task<string> ExchangeForTokenAsync(string key, string origin)
    {
        var client = _fixture.CreateWidgetClient(origin);
        var response = await PostWithHeadersAsync(client, "/api/widget/auth", new { origin }, widgetKey: key);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("accessToken").GetString()!;
    }
}

/// <summary>Shared host + widget-state reset for the widget test class.</summary>
public sealed class WidgetApiFixture : IAsyncLifetime
{
    public LmKitApiFactory Factory { get; } = new();
    public HttpClient JwtClient { get; private set; } = null!;

    public const string DefaultOrigin = "https://shop.example.com";
    public const string OtherOrigin = "https://evil.example.com";

    public async Task InitializeAsync()
    {
        Factory.EnsureSeeded();
        JwtClient = Factory.CreateClient(new WebApplicationFactoryClientOptions
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
        Factory.Dispose();
        return Task.CompletedTask;
    }

    public HttpClient CreateWidgetClient(string origin)
    {
        var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Widget-Origin", origin);
        return client;
    }

    public void ResetWidgetState()
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HermesDbContext>();
        db.TenantWidgetSettings.RemoveRange(db.TenantWidgetSettings);
        db.SaveChanges();
        // Quota counters live for the process (Redis-less hosts count locally), so a
        // previous test's turns must not eat into this one's budget.
        WidgetQuotaService.ResetLocalCountersForTests(LmKitApiFactory.TenantId);
    }

    /// <summary>Creates an ACTIVE widget settings row (optionally with a raw key) and returns the raw key (or "").</summary>
    public async Task<string> SeedActiveWidgetAsync(
        string[] allowedOrigins,
        string? title = null,
        string? brandColor = null,
        string? welcome = null,
        int requestsPerMinute = 0,
        int requestsPerDay = 0)
    {
        return await SeedWidgetAsync(
            isActive: true, allowedOrigins, issueKey: true, title, brandColor, welcome, requestsPerMinute, requestsPerDay);
    }

    public async Task<string> SeedWidgetAsync(
        bool isActive,
        string[] allowedOrigins,
        bool issueKey,
        string? title = null,
        string? brandColor = null,
        string? welcome = null,
        int requestsPerMinute = 0,
        int requestsPerDay = 0)
    {
        var rawKey = issueKey ? WidgetSecrets.Generate() : string.Empty;
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HermesDbContext>();
        db.TenantWidgetSettings.Add(new TenantWidgetSettings
        {
            TenantId = LmKitApiFactory.TenantId,
            IsActive = isActive,
            AllowedOriginsJson = WidgetOrigins.Serialize(allowedOrigins),
            WidgetApiKeyHash = issueKey ? WidgetSecrets.Hash(rawKey) : string.Empty,
            WidgetTitle = title,
            BrandColor = brandColor,
            WelcomeMessage = welcome,
            RequestsPerMinute = requestsPerMinute,
            RequestsPerDay = requestsPerDay
        });
        await db.SaveChangesAsync();
        return rawKey;
    }

    /// <summary>Overwrites the seeded row's key hash (used by the disabled-widget test).</summary>
    public async Task SetKeyHashAsync(string hash)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HermesDbContext>();
        var settings = await db.TenantWidgetSettings.SingleAsync();
        settings.WidgetApiKeyHash = hash;
        await db.SaveChangesAsync();
    }

    public async Task<TenantWidgetSettings?> GetSettingsAsync()
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HermesDbContext>();
        return await db.TenantWidgetSettings.SingleOrDefaultAsync();
    }

    /// <summary>Rotates the key through the REAL admin HTTP endpoint; returns the new raw key.</summary>
    public async Task<string> RotateViaAdminApiAsync()
    {
        var response = await JwtClient.PostAsync("/api/admin/widget/credentials:rotate", content: null);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("rawKey").GetString()!;
    }
}
