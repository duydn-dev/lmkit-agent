using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LmKitOmniApi.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LmKitOmniApi.Tests;

/// <summary>
/// Shares ONE factory and ONE login across every test in the class so the whole
/// suite stays far below the LoginPolicy limit (5 logins / 10s / IP): all further
/// requests ride the cookie or an X-Api-Key header.
/// </summary>
public sealed class ApiKeyAuthFixture : IAsyncLifetime
{
    public LmKitApiFactory Factory { get; } = new();
    public HttpClient JwtClient { get; private set; } = null!;

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

    /// <summary>Order-independence: every test starts from an empty key table.</summary>
    public void ResetApiKeys()
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HermesDbContext>();
        db.TenantApiKeys.RemoveRange(db.TenantApiKeys);
        db.SaveChanges();
    }

    public HttpClient CreateApiKeyClient(string rawKey)
    {
        var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", rawKey);
        return client;
    }
}

public sealed class ApiKeyAuthTests : IClassFixture<ApiKeyAuthFixture>
{
    private readonly ApiKeyAuthFixture _fixture;

    public ApiKeyAuthTests(ApiKeyAuthFixture fixture)
    {
        _fixture = fixture;
        _fixture.ResetApiKeys();
    }

    [Fact]
    public async Task CreateApiKey_ReturnsRawSecretOnce_AndStoresOnlyTheHash()
    {
        var created = await CreateKeyAsync("ci-pipeline");

        // Raw key: 32 random bytes, base64url without padding → exactly 43 chars.
        Assert.Matches("^[A-Za-z0-9_-]{43}$", created.RawKey);
        Assert.Equal("ci-pipeline", created.Body.GetProperty("name").GetString());
        Assert.True(created.Body.TryGetProperty("expiresAtUtc", out _));

        var list = await _fixture.JwtClient.GetFromJsonAsync<JsonElement[]>("/api/api-keys");
        var entry = Assert.Single(list!);
        Assert.Equal(created.Id, entry.GetProperty("id").GetGuid());
        Assert.Equal("ci-pipeline", entry.GetProperty("name").GetString());
        Assert.Equal(0, entry.GetProperty("maxRequests").GetInt32());
        Assert.Equal(0, entry.GetProperty("usedRequests").GetInt32());
        Assert.True(entry.GetProperty("isActive").GetBoolean());
        // The secret is shown once at creation and never again.
        Assert.False(entry.TryGetProperty("rawKey", out _));

        using var scope = _fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HermesDbContext>();
        var storedHash = await db.TenantApiKeys.AsNoTracking()
            .Where(key => key.Id == created.Id)
            .Select(key => key.ApiKey)
            .SingleAsync();
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(created.RawKey))), storedHash);
        Assert.NotEqual(created.RawKey, storedHash);
    }

    [Fact]
    public async Task XApiKeyHeader_AuthenticatesWithoutJwt_AndCountsUsageOncePerRequest()
    {
        var created = await CreateKeyAsync("automation");
        using var apiClient = _fixture.CreateApiKeyClient(created.RawKey);

        var sessions = await apiClient.GetAsync("/api/chat/sessions");
        var me = await apiClient.GetAsync("/api/auth/me");
        var meBody = await me.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, sessions.StatusCode);
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        // The principal carries the owning user's identity claims.
        Assert.Equal(LmKitApiFactory.UserId, meBody.GetProperty("id").GetGuid());
        Assert.Equal(LmKitApiFactory.TenantId, meBody.GetProperty("tenantId").GetGuid());

        // Exactly one UsedRequests increment per authenticated request, even though
        // the default policy evaluates both schemes.
        var list = await _fixture.JwtClient.GetFromJsonAsync<JsonElement[]>("/api/api-keys");
        Assert.Equal(2, Assert.Single(list!).GetProperty("usedRequests").GetInt32());
    }

    [Fact]
    public async Task AdminSurfaces_CatalogAndMetrics_AcceptBothSchemes()
    {
        var created = await CreateKeyAsync("admin-key");
        using var apiClient = _fixture.CreateApiKeyClient(created.RawKey);
        using var anonymous = _fixture.Factory.CreateClient();

        var catalogViaJwt = await _fixture.JwtClient.GetAsync("/api/mcp-servers/catalog");
        var catalogViaKey = await apiClient.GetAsync("/api/mcp-servers/catalog");
        var metricsViaKey = await apiClient.GetAsync("/metrics");
        var metricsAnonymous = await anonymous.GetAsync("/metrics");
        var catalog = await catalogViaJwt.Content.ReadFromJsonAsync<JsonElement[]>();

        Assert.Equal(HttpStatusCode.OK, catalogViaJwt.StatusCode);
        Assert.Equal(HttpStatusCode.OK, catalogViaKey.StatusCode);
        Assert.InRange(catalog!.Length, 4, 6);
        Assert.All(catalog, entry =>
        {
            Assert.False(string.IsNullOrWhiteSpace(entry.GetProperty("name").GetString()));
            Assert.StartsWith("https://", entry.GetProperty("baseUrl").GetString());
            Assert.False(string.IsNullOrWhiteSpace(entry.GetProperty("description").GetString()));
        });
        // The seeded user is an Admin: the role claim must work on the ApiKey scheme
        // too (custom "Role" claim type), so the /metrics gate lets the key through
        // while anonymous callers stay locked out.
        Assert.Equal(HttpStatusCode.OK, metricsViaKey.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, metricsAnonymous.StatusCode);
    }

    [Fact]
    public async Task RevokedApiKey_IsRejectedImmediately()
    {
        var created = await CreateKeyAsync("to-revoke");
        using var apiClient = _fixture.CreateApiKeyClient(created.RawKey);

        var beforeRevoke = await apiClient.GetAsync("/api/chat/sessions");
        var revoke = await _fixture.JwtClient.DeleteAsync($"/api/api-keys/{created.Id}");
        var afterRevoke = await apiClient.GetAsync("/api/chat/sessions");
        var unknownRevoke = await _fixture.JwtClient.DeleteAsync($"/api/api-keys/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.OK, beforeRevoke.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, afterRevoke.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknownRevoke.StatusCode);

        var list = await _fixture.JwtClient.GetFromJsonAsync<JsonElement[]>("/api/api-keys");
        Assert.False(Assert.Single(list!).GetProperty("isActive").GetBoolean());
    }

    [Fact]
    public async Task ExpiredApiKey_IsRejected()
    {
        var created = await CreateKeyAsync("short-lived", expiresInDays: 1);
        using var apiClient = _fixture.CreateApiKeyClient(created.RawKey);
        var beforeExpiry = await apiClient.GetAsync("/api/chat/sessions");

        using (var scope = _fixture.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HermesDbContext>();
            var row = await db.TenantApiKeys.SingleAsync(key => key.Id == created.Id);
            row.ExpiresAtUtc = DateTime.UtcNow.AddMinutes(-5);
            await db.SaveChangesAsync();
        }

        var afterExpiry = await apiClient.GetAsync("/api/chat/sessions");

        Assert.Equal(HttpStatusCode.OK, beforeExpiry.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, afterExpiry.StatusCode);
    }

    [Fact]
    public async Task ApiKeyPrincipal_CannotManageApiKeys()
    {
        var created = await CreateKeyAsync("leaked-key");
        using var apiClient = _fixture.CreateApiKeyClient(created.RawKey);

        var mint = await apiClient.PostAsJsonAsync("/api/api-keys", new { name = "minted-by-key" });
        var list = await apiClient.GetAsync("/api/api-keys");
        var revoke = await apiClient.DeleteAsync($"/api/api-keys/{created.Id}");
        var mintBody = await mint.Content.ReadFromJsonAsync<JsonElement>();

        // A leaked key must never mint, enumerate, or revoke keys.
        Assert.Equal(HttpStatusCode.Forbidden, mint.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, list.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, revoke.StatusCode);
        Assert.Contains("khóa API", mintBody.GetProperty("message").GetString());

        var stillThere = await _fixture.JwtClient.GetFromJsonAsync<JsonElement[]>("/api/api-keys");
        Assert.True(Assert.Single(stillThere!).GetProperty("isActive").GetBoolean());
    }

    [Fact]
    public async Task ActiveKeyCap_RejectsSixthKey_UntilOneIsRevoked()
    {
        var first = await CreateKeyAsync("cap-1");
        for (var index = 2; index <= 5; index++)
            await CreateKeyAsync($"cap-{index}");

        var sixth = await _fixture.JwtClient.PostAsJsonAsync("/api/api-keys", new { name = "cap-6" });
        var sixthBody = await sixth.Content.ReadFromJsonAsync<JsonElement>();
        var revoke = await _fixture.JwtClient.DeleteAsync($"/api/api-keys/{first.Id}");
        var afterRevoke = await _fixture.JwtClient.PostAsJsonAsync("/api/api-keys", new { name = "cap-6" });

        Assert.Equal(HttpStatusCode.BadRequest, sixth.StatusCode);
        Assert.Contains("5", sixthBody.GetProperty("message").GetString());
        Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);
        // Revoked keys no longer count against the cap.
        Assert.Equal(HttpStatusCode.Created, afterRevoke.StatusCode);
    }

    [Fact]
    public async Task Validation_RejectsBadNameExpiryAndBudget()
    {
        var missingName = await _fixture.JwtClient.PostAsJsonAsync("/api/api-keys", new { name = "" });
        var longName = await _fixture.JwtClient.PostAsJsonAsync("/api/api-keys", new { name = new string('k', 65) });
        var zeroDays = await _fixture.JwtClient.PostAsJsonAsync("/api/api-keys", new { name = "ok", expiresInDays = 0 });
        var tooManyDays = await _fixture.JwtClient.PostAsJsonAsync("/api/api-keys", new { name = "ok", expiresInDays = 366 });
        var negativeBudget = await _fixture.JwtClient.PostAsJsonAsync("/api/api-keys", new { name = "ok", maxRequests = -1 });
        var hugeBudget = await _fixture.JwtClient.PostAsJsonAsync("/api/api-keys", new { name = "ok", maxRequests = 1_000_001 });

        Assert.Equal(HttpStatusCode.BadRequest, missingName.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, longName.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, zeroDays.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, tooManyDays.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, negativeBudget.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, hugeBudget.StatusCode);
        Assert.Contains("Tên khóa API", await ReadMessageAsync(missingName));
        Assert.Contains("expiresInDays", await ReadMessageAsync(zeroDays));
        Assert.Contains("maxRequests", await ReadMessageAsync(negativeBudget));
    }

    [Fact]
    public async Task MaxRequestsBudget_BlocksAfterExhaustion()
    {
        var created = await CreateKeyAsync("metered", maxRequests: 2);
        using var apiClient = _fixture.CreateApiKeyClient(created.RawKey);

        var firstRequest = await apiClient.GetAsync("/api/chat/sessions");
        var secondRequest = await apiClient.GetAsync("/api/chat/sessions");
        var thirdRequest = await apiClient.GetAsync("/api/chat/sessions");

        Assert.Equal(HttpStatusCode.OK, firstRequest.StatusCode);
        Assert.Equal(HttpStatusCode.OK, secondRequest.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, thirdRequest.StatusCode);
    }

    [Fact]
    public async Task JwtCookieFlow_StillWorks_AndAnonymousCallersAreStillRejected()
    {
        using var anonymous = _fixture.Factory.CreateClient();

        // The pre-existing cookie-JWT session (single fixture login) keeps working
        // unchanged after the multi-scheme wiring.
        var me = await _fixture.JwtClient.GetAsync("/api/auth/me");
        var meBody = await me.Content.ReadFromJsonAsync<JsonElement>();
        var anonymousSessions = await anonymous.GetAsync("/api/chat/sessions");
        var anonymousKeys = await anonymous.GetAsync("/api/api-keys");

        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        Assert.Equal(LmKitApiFactory.UserId, meBody.GetProperty("id").GetGuid());
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousSessions.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousKeys.StatusCode);
    }

    private sealed record CreatedKey(Guid Id, string RawKey, JsonElement Body);

    private static async Task<string> ReadMessageAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var message = body.GetProperty("message").GetString();
        Assert.False(string.IsNullOrWhiteSpace(message));
        return message!;
    }

    private async Task<CreatedKey> CreateKeyAsync(string name, int? expiresInDays = null, int? maxRequests = null)
    {
        var response = await _fixture.JwtClient.PostAsJsonAsync("/api/api-keys", new
        {
            name,
            expiresInDays,
            maxRequests
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var rawKey = body.GetProperty("rawKey").GetString();
        Assert.False(string.IsNullOrWhiteSpace(rawKey));
        return new CreatedKey(body.GetProperty("id").GetGuid(), rawKey!, body);
    }
}
