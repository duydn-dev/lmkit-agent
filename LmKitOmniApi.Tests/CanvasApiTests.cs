using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LmKitOmniApi.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LmKitOmniApi.Tests;

/// <summary>
/// Contract tests for the canvas endpoints (versioned editable artifacts).
/// Deterministic: no model involved; each test creates its own roots, so the
/// class-shared factory/database never causes cross-test interference.
/// Authenticated clients are logged in once per identity and cached for the
/// whole class: the login endpoint's LoginPolicy allows only 5 logins per
/// 10-second window per IP partition (all TestServer traffic shares one), so
/// per-test logins would trip 429s on fast runs.
/// </summary>
public sealed class CanvasApiTests : IClassFixture<LmKitApiFactory>
{
    // Sessions seeded by LmKitApiFactory.EnsureSeeded: 5555… belongs to the
    // integration user, 6666… to a different user in a different tenant.
    private static readonly Guid OwnSessionId = Guid.Parse("55555555-5555-5555-5555-555555555555");
    private static readonly Guid OtherSessionId = Guid.Parse("66666666-6666-6666-6666-666666666666");

    // One cached client per identity for the class lifetime (the fixture — and
    // therefore the server the cookies belong to — is also one per class).
    private static readonly SemaphoreSlim ClientGate = new(1, 1);
    private static HttpClient? _ownerClient;
    private static HttpClient? _otherTenantClient;
    private readonly LmKitApiFactory _factory;

    public CanvasApiTests(LmKitApiFactory factory)
    {
        _factory = factory;
        _factory.EnsureSeeded();
    }

    [Fact]
    public async Task CreateCanvas_Returns201LatestShape_AndListContainsIt()
    {
        var client = await CreateAuthenticatedClientAsync();

        var create = await client.PostAsJsonAsync("/api/canvas", new
        {
            chatSessionId = OwnSessionId,
            title = "Báo cáo doanh thu",
            kind = "markdown",
            content = "# Quý 3"
        });
        var created = await ReadJsonAsync(create, HttpStatusCode.Created);
        var rootId = created.GetProperty("rootId").GetGuid();

        // First version: the root IS the row (id == rootId, version == 1).
        Assert.Equal(created.GetProperty("id").GetGuid(), rootId);
        Assert.Equal(1, created.GetProperty("version").GetInt32());
        Assert.Equal("Báo cáo doanh thu", created.GetProperty("title").GetString());
        Assert.Equal("markdown", created.GetProperty("kind").GetString());
        Assert.Equal(JsonValueKind.Null, created.GetProperty("language").ValueKind);
        Assert.Equal("# Quý 3", created.GetProperty("content").GetString());
        Assert.Equal(OwnSessionId, created.GetProperty("chatSessionId").GetGuid());
        Assert.True(created.TryGetProperty("createdAt", out _));
        Assert.EndsWith($"/api/canvas/{rootId}", create.Headers.Location!.ToString());

        var list = await client.GetFromJsonAsync<JsonElement[]>("/api/canvas");
        var item = Assert.Single(list!, e => e.GetProperty("rootId").GetGuid() == rootId);
        Assert.Equal("Báo cáo doanh thu", item.GetProperty("title").GetString());
        Assert.Equal(1, item.GetProperty("version").GetInt32());
        Assert.Equal(OwnSessionId, item.GetProperty("chatSessionId").GetGuid());
        Assert.True(item.TryGetProperty("updatedAt", out _));
        Assert.False(item.TryGetProperty("content", out _)); // list is the light shape

        // Session filter: present under its own session, absent under a random one.
        var filtered = await client.GetFromJsonAsync<JsonElement[]>($"/api/canvas?sessionId={OwnSessionId}");
        Assert.Contains(filtered!, e => e.GetProperty("rootId").GetGuid() == rootId);
        var unrelated = await client.GetFromJsonAsync<JsonElement[]>($"/api/canvas?sessionId={Guid.NewGuid()}");
        Assert.DoesNotContain(unrelated!, e => e.GetProperty("rootId").GetGuid() == rootId);
    }

    [Fact]
    public async Task UpdateTwice_YieldsThreeVersions_LatestWinsAndVersionOneStaysReadable()
    {
        var client = await CreateAuthenticatedClientAsync();

        var create = await client.PostAsJsonAsync("/api/canvas", new
        {
            title = "Nháp",
            kind = "code",
            language = "csharp",
            content = "v1"
        });
        var rootId = (await ReadJsonAsync(create, HttpStatusCode.Created)).GetProperty("rootId").GetGuid();

        var put2 = await client.PutAsJsonAsync($"/api/canvas/{rootId}", new { content = "v2" });
        var put2Body = await ReadJsonAsync(put2, HttpStatusCode.OK);
        var put3 = await client.PutAsJsonAsync($"/api/canvas/{rootId}", new { title = "Bản cuối", content = "v3" });
        var put3Body = await ReadJsonAsync(put3, HttpStatusCode.OK);

        Assert.Equal(2, put2Body.GetProperty("version").GetInt32());
        Assert.Equal(3, put3Body.GetProperty("version").GetInt32());
        Assert.NotEqual(put2Body.GetProperty("id").GetGuid(), put3Body.GetProperty("id").GetGuid());

        var versions = await client.GetFromJsonAsync<JsonElement[]>($"/api/canvas/{rootId}/versions");
        Assert.Equal(new[] { 3, 2, 1 }, versions!.Select(v => v.GetProperty("version").GetInt32()).ToArray());
        Assert.All(versions!, v => Assert.True(v.TryGetProperty("createdAt", out _)));
        Assert.All(versions!, v => Assert.True(v.TryGetProperty("id", out _)));

        // GET latest: newest content, newest title, kind/language carried over.
        var latest = await ReadJsonAsync(await client.GetAsync($"/api/canvas/{rootId}"), HttpStatusCode.OK);
        Assert.Equal(3, latest.GetProperty("version").GetInt32());
        Assert.Equal("v3", latest.GetProperty("content").GetString());
        Assert.Equal("Bản cuối", latest.GetProperty("title").GetString());
        Assert.Equal("code", latest.GetProperty("kind").GetString());
        Assert.Equal("csharp", latest.GetProperty("language").GetString());

        // ?version=1 returns the untouched original row.
        var original = await ReadJsonAsync(await client.GetAsync($"/api/canvas/{rootId}?version=1"), HttpStatusCode.OK);
        Assert.Equal(1, original.GetProperty("version").GetInt32());
        Assert.Equal("v1", original.GetProperty("content").GetString());
        Assert.Equal("Nháp", original.GetProperty("title").GetString());

        // The title-less save (v2) carried the previous title forward.
        var second = await ReadJsonAsync(await client.GetAsync($"/api/canvas/{rootId}?version=2"), HttpStatusCode.OK);
        Assert.Equal("Nháp", second.GetProperty("title").GetString());

        // Unknown version of a known root is a plain 404.
        var missingVersion = await client.GetAsync($"/api/canvas/{rootId}?version=9");
        Assert.Equal(HttpStatusCode.NotFound, missingVersion.StatusCode);

        // The list collapses the family: exactly one entry, and it is version 3.
        var list = await client.GetFromJsonAsync<JsonElement[]>("/api/canvas");
        var entry = Assert.Single(list!, e => e.GetProperty("rootId").GetGuid() == rootId);
        Assert.Equal(3, entry.GetProperty("version").GetInt32());
        Assert.Equal("Bản cuối", entry.GetProperty("title").GetString());
    }

    [Fact]
    public async Task CanvasEndpoints_ForForeignOrMissingRoot_ReturnNotFound()
    {
        var owner = await CreateAuthenticatedClientAsync();
        var stranger = await CreateOtherTenantClientAsync();

        var create = await owner.PostAsJsonAsync("/api/canvas", new
        {
            title = "Riêng tư",
            kind = "text",
            content = "nội dung riêng"
        });
        var rootId = (await ReadJsonAsync(create, HttpStatusCode.Created)).GetProperty("rootId").GetGuid();

        var foreignGet = await stranger.GetAsync($"/api/canvas/{rootId}");
        var foreignVersions = await stranger.GetAsync($"/api/canvas/{rootId}/versions");
        var foreignPut = await stranger.PutAsJsonAsync($"/api/canvas/{rootId}", new { content = "chiếm quyền" });
        var foreignDelete = await stranger.DeleteAsync($"/api/canvas/{rootId}");
        var foreignList = await stranger.GetFromJsonAsync<JsonElement[]>("/api/canvas");

        // Never 403: a foreign root must look exactly like one that does not exist.
        Assert.Equal(HttpStatusCode.NotFound, foreignGet.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, foreignVersions.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, foreignPut.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, foreignDelete.StatusCode);
        Assert.DoesNotContain(foreignList!, e => e.GetProperty("rootId").GetGuid() == rootId);

        // The failed foreign PUT/DELETE changed nothing for the owner.
        var stillLatest = await ReadJsonAsync(await owner.GetAsync($"/api/canvas/{rootId}"), HttpStatusCode.OK);
        Assert.Equal(1, stillLatest.GetProperty("version").GetInt32());
        Assert.Equal("nội dung riêng", stillLatest.GetProperty("content").GetString());

        // Truly missing roots behave identically for the owner.
        var missingGet = await owner.GetAsync($"/api/canvas/{Guid.NewGuid()}");
        var missingPut = await owner.PutAsJsonAsync($"/api/canvas/{Guid.NewGuid()}", new { content = "x" });
        Assert.Equal(HttpStatusCode.NotFound, missingGet.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missingPut.StatusCode);
    }

    [Fact]
    public async Task DeleteCanvas_RemovesEveryVersionOfTheRoot()
    {
        var client = await CreateAuthenticatedClientAsync();

        var create = await client.PostAsJsonAsync("/api/canvas", new
        {
            title = "Sẽ xóa",
            kind = "markdown",
            content = "v1"
        });
        var rootId = (await ReadJsonAsync(create, HttpStatusCode.Created)).GetProperty("rootId").GetGuid();
        var put = await client.PutAsJsonAsync($"/api/canvas/{rootId}", new { content = "v2" });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        var delete = await client.DeleteAsync($"/api/canvas/{rootId}");
        var getAfter = await client.GetAsync($"/api/canvas/{rootId}");
        var versionsAfter = await client.GetAsync($"/api/canvas/{rootId}/versions");
        var deleteAgain = await client.DeleteAsync($"/api/canvas/{rootId}");
        var list = await client.GetFromJsonAsync<JsonElement[]>("/api/canvas");

        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, getAfter.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, versionsAfter.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, deleteAgain.StatusCode);
        Assert.DoesNotContain(list!, e => e.GetProperty("rootId").GetGuid() == rootId);

        // Both version rows are physically gone, not just hidden.
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HermesDbContext>();
        Assert.False(await db.CanvasArtifacts.AsNoTracking().AnyAsync(c => c.RootId == rootId));
    }

    [Fact]
    public async Task CreateCanvas_RejectsInvalidPayloadsWithVietnameseMessages()
    {
        var client = await CreateAuthenticatedClientAsync();

        var missingTitle = await client.PostAsJsonAsync("/api/canvas", new { kind = "markdown", content = "x" });
        var longTitle = await client.PostAsJsonAsync("/api/canvas", new { title = new string('t', 121), kind = "markdown", content = "x" });
        var badKind = await client.PostAsJsonAsync("/api/canvas", new { title = "T", kind = "html", content = "x" });
        var longLanguage = await client.PostAsJsonAsync("/api/canvas", new { title = "T", kind = "code", language = new string('l', 41), content = "x" });
        var missingContent = await client.PostAsJsonAsync("/api/canvas", new { title = "T", kind = "markdown" });
        var hugeContent = await client.PostAsJsonAsync("/api/canvas", new { title = "T", kind = "markdown", content = new string('x', 200_001) });
        var foreignSession = await client.PostAsJsonAsync("/api/canvas", new { chatSessionId = OtherSessionId, title = "T", kind = "markdown", content = "x" });
        var unknownSession = await client.PostAsJsonAsync("/api/canvas", new { chatSessionId = Guid.NewGuid(), title = "T", kind = "markdown", content = "x" });

        foreach (var response in new[] { missingTitle, longTitle, badKind, longLanguage, missingContent, hugeContent, foreignSession, unknownSession })
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        Assert.Equal("Tiêu đề không được để trống.", await ReadMessageAsync(missingTitle));
        Assert.Equal("Loại canvas không hợp lệ (markdown, code hoặc text).", await ReadMessageAsync(badKind));
        Assert.Equal("Nội dung không được để trống.", await ReadMessageAsync(missingContent));
        // Foreign and unknown sessions get the contract's exact message — and stay
        // a 400 (POST semantics), not a 404, without confirming the id exists.
        Assert.Equal("Phiên chat không hợp lệ", await ReadMessageAsync(foreignSession));
        Assert.Equal("Phiên chat không hợp lệ", await ReadMessageAsync(unknownSession));

        // PUT shares the content rules and rejects a body with no content.
        var seeded = await client.PostAsJsonAsync("/api/canvas", new { title = "Hợp lệ", kind = "text", content = "x" });
        var rootId = (await ReadJsonAsync(seeded, HttpStatusCode.Created)).GetProperty("rootId").GetGuid();
        var putMissingContent = await client.PutAsJsonAsync($"/api/canvas/{rootId}", new { title = "Chỉ tiêu đề" });
        Assert.Equal(HttpStatusCode.BadRequest, putMissingContent.StatusCode);
        Assert.Equal("Nội dung không được để trống.", await ReadMessageAsync(putMissingContent));
    }

    [Fact]
    public async Task CanvasEndpoints_RejectAnonymousCallers()
    {
        using var anonymous = _factory.CreateClient();

        var list = await anonymous.GetAsync("/api/canvas");
        var create = await anonymous.PostAsJsonAsync("/api/canvas", new { title = "T", kind = "text", content = "x" });
        var delete = await anonymous.DeleteAsync($"/api/canvas/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.Unauthorized, list.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, create.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, delete.StatusCode);
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response, HttpStatusCode expected)
    {
        Assert.Equal(expected, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    /// <summary>Reads the Vietnamese error payload's <c>message</c> property (JSON-decoded).</summary>
    private static async Task<string?> ReadMessageAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("message").GetString();
    }

    private async Task<HttpClient> CreateAuthenticatedClientAsync()
    {
        await ClientGate.WaitAsync();
        try
        {
            return _ownerClient ??= await LoginAsync(LmKitApiFactory.Email, LmKitApiFactory.Password);
        }
        finally
        {
            ClientGate.Release();
        }
    }

    /// <summary>The seeded second user (different tenant AND user) from LmKitApiFactory.EnsureSeeded.</summary>
    private async Task<HttpClient> CreateOtherTenantClientAsync()
    {
        await ClientGate.WaitAsync();
        try
        {
            return _otherTenantClient ??= await LoginAsync("other@example.test", "Other-2026!");
        }
        finally
        {
            ClientGate.Release();
        }
    }

    private async Task<HttpClient> LoginAsync(string email, string password)
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true
        });
        var login = await client.PostAsJsonAsync("/api/auth/login", new { email, password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        return client;
    }
}
