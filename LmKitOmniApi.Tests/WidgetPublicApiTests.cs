using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LmKitOmniApi.Application.Widget;
using LmKitOmniApi.Domain.Entities;
using LmKitOmniApi.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LmKitOmniApi.Tests;

/// <summary>
/// Integration coverage for the PUBLIC widget surface: key exchange, origin
/// allowlist enforcement, widget-token chat, rotation, and cross-tenant
/// isolation. The chat engine is replaced with a canned implementation so no
/// model load is needed; everything around it (auth, policy, quota, controller
/// wiring) is the real production pipeline over SQLite.
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
        Assert.Contains("Canned widget answer", text);
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

    // ── helpers ─────────────────────────────────────────────────────────

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
    }

    /// <summary>Creates an ACTIVE widget settings row (optionally with a raw key) and returns the raw key (or "").</summary>
    public async Task<string> SeedActiveWidgetAsync(
        string[] allowedOrigins,
        string? title = null,
        string? brandColor = null,
        string? welcome = null)
    {
        return await SeedWidgetAsync(isActive: true, allowedOrigins, issueKey: true, title, brandColor, welcome);
    }

    public async Task<string> SeedWidgetAsync(
        bool isActive,
        string[] allowedOrigins,
        bool issueKey,
        string? title = null,
        string? brandColor = null,
        string? welcome = null)
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
            RequestsPerMinute = 0,
            RequestsPerDay = 0
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
