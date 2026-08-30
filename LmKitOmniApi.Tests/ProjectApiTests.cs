using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace LmKitOmniApi.Tests;

/// <summary>
/// Contract tests for the project endpoints (ChatGPT-Projects style: chat
/// sessions grouped under shared instructions) and for the projectId extensions
/// of the chat-session endpoints. Deterministic: no model involved; each test
/// creates its own projects/sessions, so the class-shared factory/database never
/// causes cross-test interference. Authenticated clients are logged in once per
/// identity and cached for the whole class: the login endpoint's LoginPolicy
/// allows only 5 logins per 10-second window per IP partition (all TestServer
/// traffic shares one), so per-test logins would trip 429s on fast runs.
/// </summary>
public sealed class ProjectApiTests : IClassFixture<LmKitApiFactory>
{
    // One cached client per identity for the class lifetime (the fixture — and
    // therefore the server the cookies belong to — is also one per class).
    private static readonly SemaphoreSlim ClientGate = new(1, 1);
    private static HttpClient? _ownerClient;
    private static HttpClient? _otherTenantClient;
    private readonly LmKitApiFactory _factory;

    public ProjectApiTests(LmKitApiFactory factory)
    {
        _factory = factory;
        _factory.EnsureSeeded();
    }

    [Fact]
    public async Task CreateProject_Returns201Dto_AndListShowsItNewestFirstWithZeroSessions()
    {
        var client = await CreateAuthenticatedClientAsync();

        var create = await client.PostAsJsonAsync("/api/projects", new
        {
            name = "Kế hoạch Quý 4",
            description = "Toàn bộ chat về kế hoạch quý 4",
            icon = "📊",
            instructions = "Luôn trả lời ngắn gọn bằng tiếng Việt."
        });
        var created = await ReadJsonAsync(create, HttpStatusCode.Created);
        var projectId = created.GetProperty("id").GetGuid();

        Assert.NotEqual(Guid.Empty, projectId);
        Assert.Equal("Kế hoạch Quý 4", created.GetProperty("name").GetString());
        Assert.Equal("Toàn bộ chat về kế hoạch quý 4", created.GetProperty("description").GetString());
        Assert.Equal("📊", created.GetProperty("icon").GetString());
        Assert.Equal("Luôn trả lời ngắn gọn bằng tiếng Việt.", created.GetProperty("instructions").GetString());
        Assert.Equal(0, created.GetProperty("sessionCount").GetInt32());
        Assert.True(created.TryGetProperty("createdAt", out _));
        Assert.True(created.TryGetProperty("updatedAt", out _));

        // Optional fields may be omitted entirely and come back null.
        var minimal = await client.PostAsJsonAsync("/api/projects", new { name = "Dự án tối giản" });
        var minimalBody = await ReadJsonAsync(minimal, HttpStatusCode.Created);
        var minimalId = minimalBody.GetProperty("id").GetGuid();
        Assert.Equal(JsonValueKind.Null, minimalBody.GetProperty("description").ValueKind);
        Assert.Equal(JsonValueKind.Null, minimalBody.GetProperty("icon").ValueKind);
        Assert.Equal(JsonValueKind.Null, minimalBody.GetProperty("instructions").ValueKind);

        // The list contains both, newest first (the later create precedes the earlier).
        var list = await client.GetFromJsonAsync<JsonElement[]>("/api/projects");
        var first = Assert.Single(list!, e => e.GetProperty("id").GetGuid() == projectId);
        Assert.Equal(0, first.GetProperty("sessionCount").GetInt32());
        var olderIndex = Array.FindIndex(list!, e => e.GetProperty("id").GetGuid() == projectId);
        var newerIndex = Array.FindIndex(list!, e => e.GetProperty("id").GetGuid() == minimalId);
        Assert.True(newerIndex >= 0 && olderIndex >= 0);
        Assert.True(newerIndex < olderIndex, "GET /api/projects must order newest first");
    }

    [Fact]
    public async Task CreateSession_WithProjectId_BindsFiltersAndCountsSessions()
    {
        var client = await CreateAuthenticatedClientAsync();

        var projectId = await CreateProjectAsync(client, "Dự án gắn phiên");

        // Bind a new session to the project.
        var createBound = await client.PostAsJsonAsync("/api/chat/sessions", new { projectId });
        var boundSession = await ReadJsonAsync(createBound, HttpStatusCode.OK);
        var boundSessionId = boundSession.GetProperty("id").GetGuid();
        Assert.Equal(projectId, boundSession.GetProperty("projectId").GetGuid());

        // A legacy body-less create keeps working and stays unbound.
        var createPlain = await client.PostAsync("/api/chat/sessions", null);
        var plainSession = await ReadJsonAsync(createPlain, HttpStatusCode.OK);
        var plainSessionId = plainSession.GetProperty("id").GetGuid();
        Assert.Equal(JsonValueKind.Null, plainSession.GetProperty("projectId").ValueKind);

        // The project sessions endpoint returns exactly the bound session, in the
        // main list's DTO shape.
        var projectSessions = await client.GetFromJsonAsync<JsonElement[]>($"/api/projects/{projectId}/sessions");
        var inProject = Assert.Single(projectSessions!);
        Assert.Equal(boundSessionId, inProject.GetProperty("id").GetGuid());
        Assert.Equal(projectId, inProject.GetProperty("projectId").GetGuid());
        Assert.True(inProject.TryGetProperty("title", out _));
        Assert.True(inProject.TryGetProperty("createdAt", out _));
        Assert.True(inProject.TryGetProperty("customAgentId", out _));

        // ?projectId= filters the main list; without it both sessions appear.
        var filtered = await client.GetFromJsonAsync<JsonElement[]>($"/api/chat/sessions?projectId={projectId}");
        Assert.Contains(filtered!, e => e.GetProperty("id").GetGuid() == boundSessionId);
        Assert.DoesNotContain(filtered!, e => e.GetProperty("id").GetGuid() == plainSessionId);
        var unrelated = await client.GetFromJsonAsync<JsonElement[]>($"/api/chat/sessions?projectId={Guid.NewGuid()}");
        Assert.DoesNotContain(unrelated!, e => e.GetProperty("id").GetGuid() == boundSessionId);
        var unfiltered = await client.GetFromJsonAsync<JsonElement[]>("/api/chat/sessions");
        Assert.Contains(unfiltered!, e => e.GetProperty("id").GetGuid() == boundSessionId);
        Assert.Contains(unfiltered!, e => e.GetProperty("id").GetGuid() == plainSessionId);

        // The project list now reports one session for the project.
        var projects = await client.GetFromJsonAsync<JsonElement[]>("/api/projects");
        var entry = Assert.Single(projects!, e => e.GetProperty("id").GetGuid() == projectId);
        Assert.Equal(1, entry.GetProperty("sessionCount").GetInt32());

        // Binding to an unknown project is rejected with the contract's message.
        var unknownBind = await client.PostAsJsonAsync("/api/chat/sessions", new { projectId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.BadRequest, unknownBind.StatusCode);
        Assert.Equal("Dự án không tồn tại hoặc bạn không có quyền dùng", await ReadMessageAsync(unknownBind));
    }

    [Fact]
    public async Task UpdateProject_Returns204_ReplacesFieldsAndStampsUpdatedAt()
    {
        var client = await CreateAuthenticatedClientAsync();

        var create = await client.PostAsJsonAsync("/api/projects", new
        {
            name = "Trước khi sửa",
            description = "Mô tả cũ",
            icon = "📁",
            instructions = "Hướng dẫn cũ"
        });
        var created = await ReadJsonAsync(create, HttpStatusCode.Created);
        var projectId = created.GetProperty("id").GetGuid();
        var originalUpdatedAt = created.GetProperty("updatedAt").GetDateTime();

        // PUT is a full replace: omitted optional fields become null.
        var update = await client.PutAsJsonAsync($"/api/projects/{projectId}", new
        {
            name = "Sau khi sửa",
            instructions = "Hướng dẫn mới"
        });
        Assert.Equal(HttpStatusCode.NoContent, update.StatusCode);

        var list = await client.GetFromJsonAsync<JsonElement[]>("/api/projects");
        var entry = Assert.Single(list!, e => e.GetProperty("id").GetGuid() == projectId);
        Assert.Equal("Sau khi sửa", entry.GetProperty("name").GetString());
        Assert.Equal("Hướng dẫn mới", entry.GetProperty("instructions").GetString());
        Assert.Equal(JsonValueKind.Null, entry.GetProperty("description").ValueKind);
        Assert.Equal(JsonValueKind.Null, entry.GetProperty("icon").ValueKind);
        Assert.True(entry.GetProperty("updatedAt").GetDateTime() >= originalUpdatedAt);
    }

    [Fact]
    public async Task ProjectEndpoints_ForForeignOrMissingProject_ReturnNotFound()
    {
        var owner = await CreateAuthenticatedClientAsync();
        var stranger = await CreateOtherTenantClientAsync();

        var projectId = await CreateProjectAsync(owner, "Riêng tư");

        // Never 403: a foreign project must look exactly like one that does not exist.
        var foreignPut = await stranger.PutAsJsonAsync($"/api/projects/{projectId}", new { name = "Chiếm quyền" });
        var foreignDelete = await stranger.DeleteAsync($"/api/projects/{projectId}");
        var foreignSessions = await stranger.GetAsync($"/api/projects/{projectId}/sessions");
        var foreignList = await stranger.GetFromJsonAsync<JsonElement[]>("/api/projects");

        Assert.Equal(HttpStatusCode.NotFound, foreignPut.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, foreignDelete.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, foreignSessions.StatusCode);
        Assert.DoesNotContain(foreignList!, e => e.GetProperty("id").GetGuid() == projectId);

        // Binding a session to a foreign project fails like an unknown one (400,
        // same message — the id's existence is never confirmed).
        var foreignBind = await stranger.PostAsJsonAsync("/api/chat/sessions", new { projectId });
        Assert.Equal(HttpStatusCode.BadRequest, foreignBind.StatusCode);
        Assert.Equal("Dự án không tồn tại hoặc bạn không có quyền dùng", await ReadMessageAsync(foreignBind));

        // The failed foreign PUT changed nothing for the owner.
        var ownerList = await owner.GetFromJsonAsync<JsonElement[]>("/api/projects");
        var entry = Assert.Single(ownerList!, e => e.GetProperty("id").GetGuid() == projectId);
        Assert.Equal("Riêng tư", entry.GetProperty("name").GetString());

        // Truly missing projects behave identically for the owner.
        Assert.Equal(HttpStatusCode.NotFound,
            (await owner.PutAsJsonAsync($"/api/projects/{Guid.NewGuid()}", new { name = "X" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.DeleteAsync($"/api/projects/{Guid.NewGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync($"/api/projects/{Guid.NewGuid()}/sessions")).StatusCode);
    }

    [Fact]
    public async Task DeleteProject_Returns204_AndSessionsSurviveWithProjectIdCleared()
    {
        var client = await CreateAuthenticatedClientAsync();

        var projectId = await CreateProjectAsync(client, "Sẽ xóa");
        var createSession = await client.PostAsJsonAsync("/api/chat/sessions", new { projectId });
        var sessionId = (await ReadJsonAsync(createSession, HttpStatusCode.OK)).GetProperty("id").GetGuid();

        var delete = await client.DeleteAsync($"/api/projects/{projectId}");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);

        // The session survives the project (FK SetNull) and is simply unbound now.
        var sessions = await client.GetFromJsonAsync<JsonElement[]>("/api/chat/sessions");
        var survivor = Assert.Single(sessions!, e => e.GetProperty("id").GetGuid() == sessionId);
        Assert.Equal(JsonValueKind.Null, survivor.GetProperty("projectId").ValueKind);

        // The project itself is gone everywhere.
        var list = await client.GetFromJsonAsync<JsonElement[]>("/api/projects");
        Assert.DoesNotContain(list!, e => e.GetProperty("id").GetGuid() == projectId);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/projects/{projectId}/sessions")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"/api/projects/{projectId}")).StatusCode);
    }

    [Fact]
    public async Task CreateOrUpdateProject_RejectsInvalidPayloadsWithVietnameseMessages()
    {
        var client = await CreateAuthenticatedClientAsync();

        var missingName = await client.PostAsJsonAsync("/api/projects", new { description = "x" });
        var blankName = await client.PostAsJsonAsync("/api/projects", new { name = "   " });
        var longName = await client.PostAsJsonAsync("/api/projects", new { name = new string('n', 81) });
        var longDescription = await client.PostAsJsonAsync("/api/projects", new { name = "T", description = new string('d', 301) });
        var longIcon = await client.PostAsJsonAsync("/api/projects", new { name = "T", icon = new string('i', 17) });
        var longInstructions = await client.PostAsJsonAsync("/api/projects", new { name = "T", instructions = new string('h', 4001) });

        foreach (var response in new[] { missingName, blankName, longName, longDescription, longIcon, longInstructions })
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        Assert.Equal("Tên dự án là bắt buộc.", await ReadMessageAsync(missingName));
        Assert.Equal("Tên dự án là bắt buộc.", await ReadMessageAsync(blankName));
        Assert.Equal("Tên dự án không được vượt quá 80 ký tự.", await ReadMessageAsync(longName));
        Assert.Equal("Mô tả không được vượt quá 300 ký tự.", await ReadMessageAsync(longDescription));
        Assert.Equal("Icon không được vượt quá 16 ký tự.", await ReadMessageAsync(longIcon));
        Assert.Equal("Hướng dẫn không được vượt quá 4000 ký tự.", await ReadMessageAsync(longInstructions));

        // PUT runs the exact same rules.
        var projectId = await CreateProjectAsync(client, "Hợp lệ");
        var putMissingName = await client.PutAsJsonAsync($"/api/projects/{projectId}", new { instructions = "x" });
        Assert.Equal(HttpStatusCode.BadRequest, putMissingName.StatusCode);
        Assert.Equal("Tên dự án là bắt buộc.", await ReadMessageAsync(putMissingName));
    }

    [Fact]
    public async Task CreateProject_EnforcesPerUserCapOfTwenty()
    {
        // Uses the other-tenant identity so the cap never collides with the
        // owner-side projects the rest of the class creates.
        var stranger = await CreateOtherTenantClientAsync();

        var existing = await stranger.GetFromJsonAsync<JsonElement[]>("/api/projects");
        for (var i = existing!.Length; i < 20; i++)
        {
            var fill = await stranger.PostAsJsonAsync("/api/projects", new { name = $"Dự án {i}" });
            Assert.Equal(HttpStatusCode.Created, fill.StatusCode);
        }

        var overflow = await stranger.PostAsJsonAsync("/api/projects", new { name = "Quá giới hạn" });
        Assert.Equal(HttpStatusCode.BadRequest, overflow.StatusCode);
        Assert.Equal("Bạn đã đạt giới hạn tối đa 20 dự án.", await ReadMessageAsync(overflow));
    }

    [Fact]
    public async Task ProjectEndpoints_RejectAnonymousCallers()
    {
        using var anonymous = _factory.CreateClient();

        var list = await anonymous.GetAsync("/api/projects");
        var create = await anonymous.PostAsJsonAsync("/api/projects", new { name = "T" });
        var update = await anonymous.PutAsJsonAsync($"/api/projects/{Guid.NewGuid()}", new { name = "T" });
        var delete = await anonymous.DeleteAsync($"/api/projects/{Guid.NewGuid()}");
        var sessions = await anonymous.GetAsync($"/api/projects/{Guid.NewGuid()}/sessions");

        Assert.Equal(HttpStatusCode.Unauthorized, list.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, create.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, update.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, delete.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, sessions.StatusCode);
    }

    private static async Task<Guid> CreateProjectAsync(HttpClient client, string name)
    {
        var create = await client.PostAsJsonAsync("/api/projects", new
        {
            name,
            instructions = "Luôn trả lời bằng tiếng Việt."
        });
        return (await ReadJsonAsync(create, HttpStatusCode.Created)).GetProperty("id").GetGuid();
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
