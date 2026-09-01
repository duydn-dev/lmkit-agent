using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LmKitOmniApi.Domain.Entities;
using LmKitOmniApi.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace LmKitOmniApi.Tests;

/// <summary>
/// Contract tests for the admin activity-log endpoints (GET /api/audit and
/// /api/audit/facets). The audit table is written by an interceptor on every
/// SaveChanges, so assertions key off unique per-test <c>Action</c> markers
/// rather than exact totals. The two seeded identities are both Admin but live
/// in different tenants, which lets us prove the read is strictly tenant-scoped.
/// </summary>
public sealed class AuditApiTests : IClassFixture<LmKitApiFactory>
{
    private static readonly SemaphoreSlim ClientGate = new(1, 1);
    private static HttpClient? _ownerClient;
    private static HttpClient? _otherTenantClient;

    private static readonly Guid OtherTenantId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private readonly LmKitApiFactory _factory;

    public AuditApiTests(LmKitApiFactory factory)
    {
        _factory = factory;
        _factory.EnsureSeeded();
    }

    [Fact]
    public async Task List_IsTenantScoped_FiltersByAction_AndPaginates()
    {
        var marker = $"TEST.Audit.{Guid.NewGuid():N}";
        SeedAuditLogs(LmKitApiFactory.TenantId, marker, count: 3, entityType: "run_python");
        SeedAuditLogs(OtherTenantId, marker, count: 1, entityType: "run_python");

        var client = await OwnerClientAsync();

        // Filter by our marker: only this tenant's three rows come back.
        var page = await client.GetFromJsonAsync<JsonElement>($"/api/audit?action={marker}");
        Assert.Equal(3, page.GetProperty("total").GetInt32());
        var items = page.GetProperty("items");
        Assert.Equal(3, items.GetArrayLength());
        foreach (var item in items.EnumerateArray())
        {
            Assert.Equal(marker, item.GetProperty("action").GetString());
            Assert.Equal("run_python", item.GetProperty("entityType").GetString());
        }

        // Paging: pageSize 2 returns the first two of three, total unchanged.
        var firstPage = await client.GetFromJsonAsync<JsonElement>($"/api/audit?action={marker}&page=1&pageSize=2");
        Assert.Equal(3, firstPage.GetProperty("total").GetInt32());
        Assert.Equal(2, firstPage.GetProperty("items").GetArrayLength());
        Assert.Equal(2, firstPage.GetProperty("pageSize").GetInt32());

        var secondPage = await client.GetFromJsonAsync<JsonElement>($"/api/audit?action={marker}&page=2&pageSize=2");
        Assert.Equal(1, secondPage.GetProperty("items").GetArrayLength());
        Assert.Equal(2, secondPage.GetProperty("page").GetInt32());
    }

    [Fact]
    public async Task List_NeverLeaksAnotherTenantsRows()
    {
        var ownMarker = $"TEST.Audit.Own.{Guid.NewGuid():N}";
        var foreignMarker = $"TEST.Audit.Foreign.{Guid.NewGuid():N}";
        SeedAuditLogs(LmKitApiFactory.TenantId, ownMarker, count: 1, entityType: "own_tool");
        SeedAuditLogs(OtherTenantId, foreignMarker, count: 2, entityType: "foreign_tool");

        var owner = await OwnerClientAsync();

        // Owner sees their own marker...
        var own = await owner.GetFromJsonAsync<JsonElement>($"/api/audit?action={ownMarker}");
        Assert.Equal(1, own.GetProperty("total").GetInt32());

        // ...but the foreign tenant's marker is invisible, even to another Admin.
        var foreign = await owner.GetFromJsonAsync<JsonElement>($"/api/audit?action={foreignMarker}");
        Assert.Equal(0, foreign.GetProperty("total").GetInt32());
        Assert.Equal(0, foreign.GetProperty("items").GetArrayLength());

        // The other tenant's Admin sees the mirror image.
        var other = await OtherTenantClientAsync();
        var otherView = await other.GetFromJsonAsync<JsonElement>($"/api/audit?action={foreignMarker}");
        Assert.Equal(2, otherView.GetProperty("total").GetInt32());
        var otherOwnView = await other.GetFromJsonAsync<JsonElement>($"/api/audit?action={ownMarker}");
        Assert.Equal(0, otherOwnView.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Facets_ReturnDistinctTenantScopedValues()
    {
        var marker = $"TEST.Facet.{Guid.NewGuid():N}";
        var foreignMarker = $"TEST.Facet.Foreign.{Guid.NewGuid():N}";
        SeedAuditLogs(LmKitApiFactory.TenantId, marker, count: 2, entityType: "facet_tool");
        SeedAuditLogs(OtherTenantId, foreignMarker, count: 1, entityType: "foreign_facet_tool");

        var client = await OwnerClientAsync();
        var facets = await client.GetFromJsonAsync<JsonElement>("/api/audit/facets");

        var actions = facets.GetProperty("actions").EnumerateArray().Select(a => a.GetString()).ToList();
        Assert.Contains(marker, actions);
        Assert.DoesNotContain(foreignMarker, actions);

        var entityTypes = facets.GetProperty("entityTypes").EnumerateArray().Select(a => a.GetString()).ToList();
        Assert.Contains("facet_tool", entityTypes);
        Assert.DoesNotContain("foreign_facet_tool", entityTypes);
    }

    [Fact]
    public async Task AuditEndpoints_RejectAnonymousCallers()
    {
        using var anonymous = _factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/audit")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/audit/facets")).StatusCode);
    }

    private void SeedAuditLogs(Guid tenantId, string action, int count, string entityType)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HermesDbContext>();
        for (var i = 0; i < count; i++)
        {
            db.AuditLogs.Add(new AuditLog
            {
                TenantId = tenantId,
                ActorType = "agent",
                Action = action,
                EntityType = entityType,
                DetailsJson = $"{{\"index\":{i}}}"
            });
        }
        db.SaveChanges();
    }

    private async Task<HttpClient> OwnerClientAsync()
    {
        await ClientGate.WaitAsync();
        try { return _ownerClient ??= await LoginAsync(LmKitApiFactory.Email, LmKitApiFactory.Password); }
        finally { ClientGate.Release(); }
    }

    private async Task<HttpClient> OtherTenantClientAsync()
    {
        await ClientGate.WaitAsync();
        try { return _otherTenantClient ??= await LoginAsync("other@example.test", "Other-2026!"); }
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
