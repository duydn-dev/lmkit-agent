using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace LmKitOmniApi.Tests;

/// <summary>
/// Contract tests for the admin database-connection endpoints: admin-only,
/// tenant-scoped, the connection string is never returned, egress refuses an
/// internal Postgres target, and a valid SQLite file tests green.
/// </summary>
[Collection("DbSqlite")]
public sealed class DatabaseConnectionsApiTests : IClassFixture<LmKitApiFactory>
{
    private static readonly SemaphoreSlim ClientGate = new(1, 1);
    private static HttpClient? _ownerClient;

    private readonly LmKitApiFactory _factory;

    public DatabaseConnectionsApiTests(LmKitApiFactory factory)
    {
        _factory = factory;
        _factory.EnsureSeeded();
    }

    [Fact]
    public async Task Create_Then_List_ExposesMetadata_ButNeverTheConnectionString()
    {
        var client = await OwnerClientAsync();

        var create = await client.PostAsJsonAsync("/api/database-connections", new
        {
            name = $"pg-{Guid.NewGuid():N}",
            provider = "Postgres",
            connectionString = "Host=db.example.com;Port=5432;Database=app;Username=readonly;Password=secret-pw",
            isActive = true
        });
        Assert.Equal(HttpStatusCode.OK, create.StatusCode);
        var id = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var listResponse = await client.GetAsync("/api/database-connections");
        var rawBody = await listResponse.Content.ReadAsStringAsync();
        Assert.DoesNotContain("secret-pw", rawBody);
        Assert.DoesNotContain("connectionString", rawBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Password", rawBody, StringComparison.OrdinalIgnoreCase);

        // Getlist chuẩn hoá: trang PagedResult { items, page, pageSize, totalCount }.
        var page = JsonSerializer.Deserialize<JsonElement>(rawBody);
        var list = page.GetProperty("items").EnumerateArray().ToArray();
        Assert.True(page.GetProperty("totalCount").GetInt32() >= 1);
        var entry = Assert.Single(list, e => e.GetProperty("id").GetGuid() == id);
        Assert.Equal("Postgres", entry.GetProperty("provider").GetString());
    }

    [Fact]
    public async Task Create_RejectsBlankNameAndUnsupportedProvider()
    {
        var client = await OwnerClientAsync();

        var blank = await client.PostAsJsonAsync("/api/database-connections",
            new { name = "  ", provider = "Postgres", connectionString = "Host=x" });
        Assert.Equal(HttpStatusCode.BadRequest, blank.StatusCode);

        // Db2 is not one of the supported engines (Postgres/Sqlite/MySql/SqlServer/Oracle).
        var badProvider = await client.PostAsJsonAsync("/api/database-connections",
            new { name = "x", provider = "Db2", connectionString = "x" });
        Assert.Equal(HttpStatusCode.BadRequest, badProvider.StatusCode);
    }

    [Fact]
    public async Task Test_RefusesAnInternalPostgresTarget_ViaEgressGuard()
    {
        var client = await OwnerClientAsync();

        var create = await client.PostAsJsonAsync("/api/database-connections", new
        {
            name = $"internal-{Guid.NewGuid():N}",
            provider = "Postgres",
            connectionString = "Host=127.0.0.1;Port=5432;Database=app;Username=u;Password=p",
            isActive = true
        });
        var id = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var test = await client.PostAsync($"/api/database-connections/{id}/test", null);
        Assert.Equal(HttpStatusCode.BadRequest, test.StatusCode);
        var message = (await test.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("message").GetString();
        Assert.Contains("nội bộ", message); // internal-address denial
    }

    [Fact]
    public async Task Test_SucceedsForAValidSqliteFile()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"lmkit-apitest-{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={dbPath};Pooling=False";
        using (var connection = new SqliteConnection(connectionString))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE t (id INTEGER PRIMARY KEY)";
            command.ExecuteNonQuery();
        }

        try
        {
            var client = await OwnerClientAsync();
            var create = await client.PostAsJsonAsync("/api/database-connections", new
            {
                name = $"sqlite-{Guid.NewGuid():N}",
                provider = "Sqlite",
                connectionString,
                isActive = true
            });
            var id = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

            var test = await client.PostAsync($"/api/database-connections/{id}/test", null);
            Assert.Equal(HttpStatusCode.OK, test.StatusCode);
        }
        finally
        {
            try { if (File.Exists(dbPath)) File.Delete(dbPath); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task Schema_ReturnsTablesColumnsAndRelations_WithoutLeakingTheSecret()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"lmkit-diagram-{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={dbPath};Pooling=False";
        using (var connection = new SqliteConnection(connectionString))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE customers (id INTEGER PRIMARY KEY, name TEXT NOT NULL);
                CREATE TABLE orders (id INTEGER PRIMARY KEY, customer_id INTEGER NOT NULL REFERENCES customers(id), total REAL);
                """;
            command.ExecuteNonQuery();
        }

        try
        {
            var client = await OwnerClientAsync();
            var create = await client.PostAsJsonAsync("/api/database-connections", new
            {
                name = $"diagram-{Guid.NewGuid():N}",
                provider = "Sqlite",
                connectionString,
                isActive = true
            });
            var id = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

            var response = await client.GetAsync($"/api/database-connections/{id}/schema");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var raw = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain("Data Source", raw);

            var body = JsonSerializer.Deserialize<JsonElement>(raw);
            Assert.Equal(2, body.GetProperty("tableCount").GetInt32());
            Assert.False(body.GetProperty("truncated").GetBoolean());

            var tables = body.GetProperty("tables").EnumerateArray().ToArray();
            var orders = tables.Single(t => t.GetProperty("name").GetString() == "orders");
            var customerId = orders.GetProperty("columns").EnumerateArray()
                .Single(c => c.GetProperty("name").GetString() == "customer_id");
            Assert.True(customerId.GetProperty("isForeignKey").GetBoolean());
            Assert.True(orders.GetProperty("columns").EnumerateArray()
                .Single(c => c.GetProperty("name").GetString() == "id")
                .GetProperty("isPrimaryKey").GetBoolean());

            // The FK is an EDGE, not just text: a client can draw it without parsing.
            var relation = Assert.Single(body.GetProperty("relations").EnumerateArray().ToArray());
            Assert.Equal("orders", relation.GetProperty("fromTable").GetString());
            Assert.Equal("customer_id", relation.GetProperty("fromColumn").GetString());
            Assert.Equal("customers", relation.GetProperty("toTable").GetString());
            Assert.Equal("id", relation.GetProperty("toColumn").GetString());
            Assert.True(relation.GetProperty("targetIncluded").GetBoolean());
        }
        finally
        {
            try { if (File.Exists(dbPath)) File.Delete(dbPath); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task Schema_RefusesAnInternalPostgresTarget_AndHidesUnknownIds()
    {
        var client = await OwnerClientAsync();

        var create = await client.PostAsJsonAsync("/api/database-connections", new
        {
            name = $"internal-diagram-{Guid.NewGuid():N}",
            provider = "Postgres",
            connectionString = "Host=127.0.0.1;Port=5432;Database=app;Username=u;Password=p",
            isActive = true
        });
        var id = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        // The diagram reads the schema live, so it must pass the SAME egress guard as a query.
        var refused = await client.GetAsync($"/api/database-connections/{id}/schema");
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("nội bộ", (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("message").GetString());

        Assert.Equal(HttpStatusCode.NotFound,
            (await client.GetAsync($"/api/database-connections/{Guid.NewGuid()}/schema")).StatusCode);
    }

    [Fact]
    public async Task Reindex_QueuesTheConnection_AndIsTenantScoped()
    {
        var client = await OwnerClientAsync();
        var create = await client.PostAsJsonAsync("/api/database-connections", new
        {
            name = $"reindex-{Guid.NewGuid():N}",
            provider = "Sqlite",
            connectionString = "Data Source=:memory:",
            isActive = true
        });
        var id = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var reindex = await client.PostAsync($"/api/database-connections/{id}/reindex", null);
        Assert.Equal(HttpStatusCode.Accepted, reindex.StatusCode);

        // The worker is disabled in tests, so it stays queued (Pending, not indexed).
        var pageDoc = await client.GetFromJsonAsync<JsonElement>("/api/database-connections");
        var entry = pageDoc.GetProperty("items").EnumerateArray().Single(e => e.GetProperty("id").GetGuid() == id);
        Assert.False(entry.GetProperty("isIndexed").GetBoolean());
        Assert.Equal("Pending", entry.GetProperty("indexStatus").GetString());

        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync($"/api/database-connections/{Guid.NewGuid()}/reindex", null)).StatusCode);
    }

    [Fact]
    public async Task Delete_ForAForeignTenant_Returns404()
    {
        var owner = await OwnerClientAsync();
        var create = await owner.PostAsJsonAsync("/api/database-connections", new
        {
            name = $"owned-{Guid.NewGuid():N}",
            provider = "Sqlite",
            connectionString = "Data Source=:memory:",
            isActive = true
        });
        var id = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var stranger = await LoginAsync("other@example.test", "Other-2026!");
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.DeleteAsync($"/api/database-connections/{id}")).StatusCode);

        // Still there for the owner.
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/api/database-connections/{id}")).StatusCode);
    }

    [Fact]
    public void AgentOrchestrator_ResolvesFromDi_IncludingTheDatabaseToolGraph()
    {
        // Resolving the scoped orchestrator constructs its whole dependency graph —
        // including the new DbQueryService → SchemaIndexingService → providers/egress
        // — so a DI misconfiguration in the DB agent is caught here, not at runtime.
        using var scope = _factory.Services.CreateScope();
        var orchestrator = scope.ServiceProvider
            .GetRequiredService<LmKitOmniApi.Application.Abstractions.IAgentOrchestrator>();
        Assert.NotNull(orchestrator);
    }

    [Fact]
    public async Task Endpoints_RejectAnonymousCallers()
    {
        using var anonymous = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/database-connections")).StatusCode);
        var post = await anonymous.PostAsJsonAsync("/api/database-connections", new { name = "x", provider = "Sqlite", connectionString = "x" });
        Assert.Equal(HttpStatusCode.Unauthorized, post.StatusCode);
    }

    private async Task<HttpClient> OwnerClientAsync()
    {
        await ClientGate.WaitAsync();
        try { return _ownerClient ??= await LoginAsync(LmKitApiFactory.Email, LmKitApiFactory.Password); }
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
