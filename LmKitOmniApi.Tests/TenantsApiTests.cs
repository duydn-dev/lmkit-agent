using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace LmKitOmniApi.Tests;

/// <summary>
/// Contract tests for the new tenant-management endpoints: paged getlist + search,
/// create/update with duplicate-name refusal, and the delete guards (never the
/// caller's own tenant, never a tenant that still holds data).
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
