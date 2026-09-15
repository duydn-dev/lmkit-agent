using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace LmKitOmniApi.Tests;

/// <summary>
/// Contract tests for the tenant-management endpoints: paged getlist + search,
/// create/update with duplicate-name refusal, the delete guards (never the
/// caller's own tenant, never a tenant that still holds data), and per-tenant
/// branding — agent display name persistence, logo upload/serve/delete with image
/// validation, and the branding surfaced on <c>GET /api/auth/me</c>.
/// </summary>
[Collection("DbSqlite")]
public sealed class TenantsApiTests : IClassFixture<LmKitApiFactory>
{
    private static readonly SemaphoreSlim ClientGate = new(1, 1);
    private static HttpClient? _adminClient;

    private readonly LmKitApiFactory _factory;

    public TenantsApiTests(LmKitApiFactory factory)
    {
        _factory = factory;
        _factory.EnsureSeeded();
    }

    [Fact]
    public async Task List_IsPaged_AndSearchFilters()
    {
        var client = await AdminClientAsync();
        var marker = $"Sở Kiểm Thử {Guid.NewGuid():N}";

        var create = await client.PostAsJsonAsync("/api/tenants", new { name = marker });
        Assert.Equal(HttpStatusCode.OK, create.StatusCode);
        var createdId = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var page = await client.GetFromJsonAsync<JsonElement>("/api/tenants?page=1&pageSize=5");
        Assert.True(page.GetProperty("totalCount").GetInt32() >= 2); // seeded tenant + this one
        Assert.Equal(1, page.GetProperty("page").GetInt32());
        Assert.Equal(5, page.GetProperty("pageSize").GetInt32());

        var filtered = await client.GetFromJsonAsync<JsonElement>($"/api/tenants?search={Uri.EscapeDataString(marker)}");
        var row = Assert.Single(filtered.GetProperty("items").EnumerateArray().ToArray());
        Assert.Equal(createdId, row.GetProperty("id").GetGuid());
        Assert.Equal(0, row.GetProperty("userCount").GetInt32());

        // Options endpoint serves dropdowns: flat id+name list containing the new tenant.
        var options = await client.GetFromJsonAsync<JsonElement[]>("/api/tenants/options");
        Assert.Contains(options!, o => o.GetProperty("id").GetGuid() == createdId);
    }

    [Fact]
    public async Task Create_RefusesBlankAndDuplicateNames()
    {
        var client = await AdminClientAsync();
        var name = $"Trùng Tên {Guid.NewGuid():N}";

        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/tenants", new { name })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/tenants", new { name })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/tenants", new { name = "  " })).StatusCode);
    }

    [Fact]
    public async Task Update_RenamesTenant_AndRefusesDuplicates()
    {
        var client = await AdminClientAsync();
        var first = $"Tenant A {Guid.NewGuid():N}";
        var second = $"Tenant B {Guid.NewGuid():N}";

        var createdFirst = await client.PostAsJsonAsync("/api/tenants", new { name = first });
        var firstId = (await createdFirst.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        await client.PostAsJsonAsync("/api/tenants", new { name = second });

        var renamed = $"{first} (đổi tên)";
        Assert.Equal(HttpStatusCode.NoContent,
            (await client.PutAsJsonAsync($"/api/tenants/{firstId}", new { name = renamed })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PutAsJsonAsync($"/api/tenants/{firstId}", new { name = second })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.PutAsJsonAsync($"/api/tenants/{Guid.NewGuid()}", new { name = "x" })).StatusCode);
    }

    [Fact]
    public async Task Delete_GuardsOwnTenant_AndNonEmptyTenants_ButRemovesEmptyOnes()
    {
        var client = await AdminClientAsync();

        // The caller's own tenant is never deletable — and it also holds data, so both
        // guards would fire; the self-guard must answer first with a clear message.
        var self = await client.DeleteAsync($"/api/tenants/{LmKitApiFactory.TenantId}");
        Assert.Equal(HttpStatusCode.BadRequest, self.StatusCode);
        var selfBody = await self.Content.ReadAsStringAsync();
        Assert.Contains("đang đăng nhập", selfBody);

        var create = await client.PostAsJsonAsync("/api/tenants", new { name = $"Tenant rỗng {Guid.NewGuid():N}" });
        var emptyId = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/tenants/{emptyId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"/api/tenants/{emptyId}")).StatusCode);
    }

    [Fact]
    public async Task AnonymousCallers_AreRejected()
    {
        using var anonymous = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/tenants")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anonymous.PostAsJsonAsync("/api/tenants", new { name = "x" })).StatusCode);
    }

    // ── Per-tenant branding ──────────────────────────────────────────────

    [Fact]
    public async Task Create_And_Update_PersistAgentDisplayName()
    {
        var client = await AdminClientAsync();
        var marker = $"Sở Trợ Lý {Guid.NewGuid():N}";

        var create = await client.PostAsJsonAsync("/api/tenants", new { name = marker, agentDisplayName = "Trợ lý A" });
        Assert.Equal(HttpStatusCode.OK, create.StatusCode);
        var id = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var row1 = SingleRow(await SearchAsync(client, marker));
        Assert.Equal("Trợ lý A", row1.GetProperty("agentDisplayName").GetString());
        Assert.False(row1.GetProperty("hasLogo").GetBoolean());

        // A blank agent name clears the override back to the system default (null).
        Assert.Equal(HttpStatusCode.NoContent,
            (await client.PutAsJsonAsync($"/api/tenants/{id}", new { name = marker, agentDisplayName = "   " })).StatusCode);
        var row2 = SingleRow(await SearchAsync(client, marker));
        Assert.Equal(JsonValueKind.Null, row2.GetProperty("agentDisplayName").ValueKind);

        // Over-length agent name is refused.
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PutAsJsonAsync($"/api/tenants/{id}", new { name = marker, agentDisplayName = new string('x', 101) })).StatusCode);
    }

    [Fact]
    public async Task Logo_Upload_Serve_Delete_RoundTrips()
    {
        var client = await AdminClientAsync();
        var marker = $"Sở Logo {Guid.NewGuid():N}";
        var id = (await (await client.PostAsJsonAsync("/api/tenants", new { name = marker }))
            .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/tenants/{id}/logo")).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent,
            (await UploadLogoAsync(client, id, FakePng(), "logo.png", "image/png")).StatusCode);

        var served = await client.GetAsync($"/api/tenants/{id}/logo");
        Assert.Equal(HttpStatusCode.OK, served.StatusCode);
        Assert.Equal("image/png", served.Content.Headers.ContentType?.MediaType);
        Assert.Equal(FakePng(), await served.Content.ReadAsByteArrayAsync());

        var row = SingleRow(await SearchAsync(client, marker));
        Assert.True(row.GetProperty("hasLogo").GetBoolean());
        Assert.NotEqual(JsonValueKind.Null, row.GetProperty("logoUpdatedAt").ValueKind);

        // Delete clears it; a second delete is idempotent (still NoContent).
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/tenants/{id}/logo")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/tenants/{id}/logo")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/tenants/{id}/logo")).StatusCode);
    }

    [Fact]
    public async Task Logo_Rejects_NonImageExtension_MismatchedSignature_AndEmpty()
    {
        var client = await AdminClientAsync();
        var id = (await (await client.PostAsJsonAsync("/api/tenants", new { name = $"Sở Ảnh {Guid.NewGuid():N}" }))
            .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        // Extension not on the allowlist.
        Assert.Equal(HttpStatusCode.BadRequest,
            (await UploadLogoAsync(client, id, [1, 2, 3], "note.txt", "text/plain")).StatusCode);
        // .png extension but the bytes are not a PNG — signature check catches it.
        Assert.Equal(HttpStatusCode.BadRequest,
            (await UploadLogoAsync(client, id, Encoding.UTF8.GetBytes("this is definitely not a png"), "fake.png", "image/png")).StatusCode);
        // Empty file.
        Assert.Equal(HttpStatusCode.BadRequest,
            (await UploadLogoAsync(client, id, [], "empty.png", "image/png")).StatusCode);
    }

    [Fact]
    public async Task Me_ReturnsTenantBranding_AndLogoServesToTheTenant()
    {
        var client = await AdminClientAsync();

        var me1 = await client.GetFromJsonAsync<JsonElement>("/api/auth/me");
        var tenant1 = me1.GetProperty("tenant");
        Assert.Equal("Integration tenant", tenant1.GetProperty("name").GetString());

        // Set the agent name + logo on the caller's OWN tenant, then restore in `finally`
        // so sibling tests still see the seeded tenant unbranded.
        Assert.Equal(HttpStatusCode.NoContent,
            (await client.PutAsJsonAsync($"/api/tenants/{LmKitApiFactory.TenantId}",
                new { name = "Integration tenant", agentDisplayName = "Trợ lý Kiểm Thử" })).StatusCode);
        try
        {
            var me2 = await client.GetFromJsonAsync<JsonElement>("/api/auth/me");
            Assert.Equal("Trợ lý Kiểm Thử", me2.GetProperty("tenant").GetProperty("agentName").GetString());
            Assert.Equal(JsonValueKind.Null, me2.GetProperty("tenant").GetProperty("logoUrl").ValueKind);
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/tenant-branding/logo")).StatusCode);

            Assert.Equal(HttpStatusCode.NoContent,
                (await UploadLogoAsync(client, LmKitApiFactory.TenantId, FakePng(), "logo.png", "image/png")).StatusCode);

            var me3 = await client.GetFromJsonAsync<JsonElement>("/api/auth/me");
            Assert.False(string.IsNullOrWhiteSpace(me3.GetProperty("tenant").GetProperty("logoUrl").GetString()));

            var served = await client.GetAsync("/api/tenant-branding/logo");
            Assert.Equal(HttpStatusCode.OK, served.StatusCode);
            Assert.Equal("image/png", served.Content.Headers.ContentType?.MediaType);
            Assert.Equal(FakePng(), await served.Content.ReadAsByteArrayAsync());
        }
        finally
        {
            await client.DeleteAsync($"/api/tenants/{LmKitApiFactory.TenantId}/logo");
            await client.PutAsJsonAsync($"/api/tenants/{LmKitApiFactory.TenantId}",
                new { name = "Integration tenant", agentDisplayName = (string?)null });
        }
    }

    [Fact]
    public async Task TenantBrandingLogo_RejectsAnonymousCallers()
    {
        using var anonymous = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/tenant-branding/logo")).StatusCode);
    }

    private static byte[] FakePng() =>
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x1, 0x2, 0x3, 0x4, 0x5, 0x6, 0x7, 0x8];

    private static Task<JsonElement> SearchAsync(HttpClient client, string marker) =>
        client.GetFromJsonAsync<JsonElement>($"/api/tenants?search={Uri.EscapeDataString(marker)}");

    private static JsonElement SingleRow(JsonElement page) =>
        Assert.Single(page.GetProperty("items").EnumerateArray().ToArray());

    private static async Task<HttpResponseMessage> UploadLogoAsync(
        HttpClient client, Guid tenantId, byte[] bytes, string fileName, string contentType)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        using var form = new MultipartFormDataContent { { content, "logo", fileName } };
        return await client.PostAsync($"/api/tenants/{tenantId}/logo", form);
    }

    private async Task<HttpClient> AdminClientAsync()
    {
        await ClientGate.WaitAsync();
        try { return _adminClient ??= await LoginAsync(LmKitApiFactory.Email, LmKitApiFactory.Password); }
        finally { ClientGate.Release(); }
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
