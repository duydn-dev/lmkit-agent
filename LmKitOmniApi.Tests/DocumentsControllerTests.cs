using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using LmKitOmniApi.Domain.Entities;
using LmKitOmniApi.Infrastructure.AI.Documents;
using LmKitOmniApi.Infrastructure.AI.Mcp;
using LmKitOmniApi.Infrastructure.AI.Security;
using LmKitOmniApi.Infrastructure.Data;
using LmKitOmniApi.Infrastructure.Data.Interceptors;
using LmKitOmniApi.Infrastructure.Workers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace LmKitOmniApi.Tests;

/// <summary>
/// Contract tests for POST /api/documents/* — the native document tools controller.
/// Covers the enable gate (501 when off), the security gates (auth, upload-size cap,
/// magic-byte validation surfaced as 400) and, when the native engine is available,
/// the end-to-end write path: a produced (filled/redacted) file lands in the caller's
/// isolated upload root and is downloadable via /api/files/{id}.
///
/// The services are registered in the TEST host (the production Program.cs wiring is
/// the coordinator's job — see PDF-INTEGRATION.md), so these tests do not depend on
/// the agent tool-graph.
/// </summary>
public sealed class DocumentsControllerTests
    : IClassFixture<DocumentsEnabledFactory>, IClassFixture<DocumentsDisabledFactory>, IClassFixture<DocumentsTinyLimitFactory>
{
    private const string PdfContentType = "application/pdf";
    private const string DocxContentType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";

    // The /api/auth/login endpoint is rate-limited, so cache one authenticated client
    // per factory instance (mirrors VisionUploadApiTests / FilesControllerTests) — each
    // host is logged into exactly once across the whole class.
    private static readonly SemaphoreSlim LoginGate = new(1, 1);
    private static readonly Dictionary<WebApplicationFactory<Program>, HttpClient> Clients = new();

    private readonly DocumentsEnabledFactory _enabled;
    private readonly DocumentsDisabledFactory _disabled;
    private readonly DocumentsTinyLimitFactory _tiny;

    public DocumentsControllerTests(
        DocumentsEnabledFactory enabled, DocumentsDisabledFactory disabled, DocumentsTinyLimitFactory tiny)
    {
        _enabled = enabled;
        _disabled = disabled;
        _tiny = tiny;
        _enabled.EnsureSeeded();
        _disabled.EnsureSeeded();
        _tiny.EnsureSeeded();
    }

    // ── Enable gate: every endpoint returns 501 when the feature is off ──

    [Fact]
    public async Task FormFields_WhenDisabled_Returns501()
    {
        var client = await LoginAsync(_disabled);
        using var form = FileForm("%PDF-1.7 x"u8.ToArray(), "in.pdf", PdfContentType);
        var response = await client.PostAsync("/api/documents/pdf/form/fields", form);
        Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode);
    }

    [Fact]
    public async Task FormFill_WhenDisabled_Returns501()
    {
        var client = await LoginAsync(_disabled);
        using var form = FileForm("%PDF-1.7 x"u8.ToArray(), "in.pdf", PdfContentType, ("values", "[]"), ("flatten", "false"));
        var response = await client.PostAsync("/api/documents/pdf/form/fill", form);
        Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode);
    }

    [Fact]
    public async Task PdfRedact_WhenDisabled_Returns501()
    {
        var client = await LoginAsync(_disabled);
        using var form = FileForm("%PDF-1.7 x"u8.ToArray(), "in.pdf", PdfContentType, ("terms", "[\"secret\"]"));
        var response = await client.PostAsync("/api/documents/pdf/redact", form);
        Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode);
    }

    [Fact]
    public async Task OfficeRedact_WhenDisabled_Returns501()
    {
        var client = await LoginAsync(_disabled);
        using var form = FileForm(new byte[] { 0x50, 0x4B, 0x03, 0x04 }, "in.docx", DocxContentType, ("terms", "[\"secret\"]"));
        var response = await client.PostAsync("/api/documents/office/redact", form);
        Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode);
    }

    [Fact]
    public async Task PdfAValidate_WhenDisabled_Returns501()
    {
        var client = await LoginAsync(_disabled);
        using var form = FileForm("%PDF-1.7 x"u8.ToArray(), "in.pdf", PdfContentType);
        var response = await client.PostAsync("/api/documents/pdf-a/validate", form);
        Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode);
    }

    // ── Auth ──

    [Fact]
    public async Task Endpoints_RejectAnonymousCallers()
    {
        using var anonymous = _enabled.CreateClient();
        using var form = FileForm("%PDF-1.7 x"u8.ToArray(), "in.pdf", PdfContentType);
        var response = await anonymous.PostAsync("/api/documents/pdf/form/fields", form);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ── Upload-size + magic-byte validation (native-free: these fail before LM-Kit) ──

    [Fact]
    public async Task FormFields_OverInputLimit_Returns400()
    {
        var client = await LoginAsync(_tiny); // MaxInputBytes = 64
        var oversize = Encoding.ASCII.GetBytes("%PDF-1.7 " + new string('a', 256));
        using var form = FileForm(oversize, "big.pdf", PdfContentType);
        var response = await client.PostAsync("/api/documents/pdf/form/fields", form);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task FormFields_NoFile_Returns400()
    {
        var client = await LoginAsync(_enabled);
        using var form = new MultipartFormDataContent(); // no "file" part
        var response = await client.PostAsync("/api/documents/pdf/form/fields", form);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PdfRedact_NonPdf_Returns400()
    {
        var client = await LoginAsync(_enabled);
        using var form = FileForm("this is not a pdf"u8.ToArray(), "in.pdf", PdfContentType, ("terms", "[\"secret\"]"));
        var response = await client.PostAsync("/api/documents/pdf/redact", form);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task OfficeRedact_NonOpenXml_Returns400()
    {
        var client = await LoginAsync(_enabled);
        // PDF bytes with a .docx name: the extension is allowed but the ZIP magic check fails.
        using var form = FileForm("%PDF-1.7 x"u8.ToArray(), "in.docx", DocxContentType, ("terms", "[\"secret\"]"));
        var response = await client.PostAsync("/api/documents/office/redact", form);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PdfAValidate_UnknownLevel_Returns400()
    {
        var client = await LoginAsync(_enabled);
        using var form = FileForm("%PDF-1.7 x"u8.ToArray(), "in.pdf", PdfContentType, ("level", "PdfA9z"));
        var response = await client.PostAsync("/api/documents/pdf-a/validate", form);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ── Real engine: happy paths (skipped only if the native engine cannot load) ──

    [SkippableFact]
    public async Task FormFields_OnNonFormPdf_Returns200_HasFormFalse()
    {
        Skip.IfNot(NativeDocumentEngine.IsAvailable, "Native LM-Kit document engine is unavailable in this host.");
        var client = await LoginAsync(_enabled);
        var pdf = NativeDocumentEngine.PdfFromMarkdown("# Plain\n\nNo form fields here.");

        using var form = FileForm(pdf, "plain.pdf", PdfContentType);
        var response = await client.PostAsync("/api/documents/pdf/form/fields", form);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("hasForm").GetBoolean());
    }

    [SkippableFact]
    public async Task FormFill_WritesOwnedFile_AndItIsDownloadable()
    {
        Skip.IfNot(NativeDocumentEngine.IsAvailable, "Native LM-Kit document engine is unavailable in this host.");
        var client = await LoginAsync(_enabled);
        var pdf = NativeDocumentEngine.PdfFromMarkdown("# Fillable-ish\n\nNo real fields, but fill must still succeed.");

        using var form = FileForm(pdf, "src.pdf", PdfContentType,
            ("values", "[{\"name\":\"missing\",\"value\":\"x\"}]"), ("flatten", "false"));
        var response = await client.PostAsync("/api/documents/pdf/form/fill", form);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var fileId = await AssertProducedFileDownloadable(client, response, PdfContentType);
        await CleanupOwnedFileAsync(fileId);
    }

    [SkippableFact]
    public async Task PdfRedact_WritesOwnedFile_AndItIsDownloadable()
    {
        Skip.IfNot(NativeDocumentEngine.IsAvailable, "Native LM-Kit document engine is unavailable in this host.");
        var client = await LoginAsync(_enabled);
        const string secret = "HTTPSECRET5150";
        var pdf = NativeDocumentEngine.PdfFromMarkdown($"# Memo\n\nContains {secret} which must be redacted.");

        using var form = FileForm(pdf, "memo.pdf", PdfContentType, ("terms", $"[\"{secret}\"]"));
        var response = await client.PostAsync("/api/documents/pdf/redact", form);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("report").GetProperty("contentRemoved").GetBoolean());

        var fileId = body.GetProperty("fileId").GetString()!;
        var download = await client.GetAsync($"/api/files/{fileId}");
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal(PdfContentType, download.Content.Headers.ContentType?.MediaType);
        await CleanupOwnedFileAsync(fileId);
    }

    [SkippableFact]
    public async Task PdfAValidate_Returns200_WithVerdict()
    {
        Skip.IfNot(NativeDocumentEngine.IsAvailable, "Native LM-Kit document engine is unavailable in this host.");
        var client = await LoginAsync(_enabled);
        var pdf = NativeDocumentEngine.PdfFromMarkdown("# Archive\n\nValidate me.");

        using var form = FileForm(pdf, "archive.pdf", PdfContentType);
        var response = await client.PostAsync("/api/documents/pdf-a/validate", form);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var verdict = body.GetProperty("verdict").GetString();
        Assert.Contains(verdict, new[] { "Compliant", "NonCompliant", "Undetermined" });
        Assert.Equal(JsonValueKind.Array, body.GetProperty("findings").ValueKind);
    }

    // ── Helpers ──

    private static async Task<string> AssertProducedFileDownloadable(HttpClient client, HttpResponseMessage response, string expectedContentType)
    {
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var fileId = body.GetProperty("fileId").GetString();
        Assert.False(string.IsNullOrWhiteSpace(fileId));
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("name").GetString()));

        var download = await client.GetAsync($"/api/files/{fileId}");
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal(expectedContentType, download.Content.Headers.ContentType?.MediaType);
        return fileId!;
    }

    private async Task CleanupOwnedFileAsync(string fileId)
    {
        using var scope = _enabled.Services.CreateScope();
        var resources = scope.ServiceProvider.GetRequiredService<UserResourceAccessService>();
        var path = Path.Combine(
            resources.GetUploadDirectory(DocumentsApiFactoryBase.TenantId, DocumentsApiFactoryBase.UserId),
            Path.GetFileName(fileId));
        if (File.Exists(path)) File.Delete(path);
        await Task.CompletedTask;
    }

    private static MultipartFormDataContent FileForm(byte[] bytes, string fileName, string contentType, params (string Name, string Value)[] fields)
    {
        var fileContent = new ByteArrayContent(bytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        var form = new MultipartFormDataContent { { fileContent, "file", fileName } };
        foreach (var (name, value) in fields)
            form.Add(new StringContent(value), name);
        return form;
    }

    private static async Task<HttpClient> LoginAsync(WebApplicationFactory<Program> factory)
    {
        await LoginGate.WaitAsync();
        try
        {
            if (Clients.TryGetValue(factory, out var cached)) return cached;

            var client = factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
                HandleCookies = true
            });
            var login = await client.PostAsJsonAsync("/api/auth/login", new
            {
                email = DocumentsApiFactoryBase.Email,
                password = DocumentsApiFactoryBase.Password
            });
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
            Clients[factory] = client;
            return client;
        }
        finally
        {
            LoginGate.Release();
        }
    }
}

/// <summary>
/// Test host for the documents controller. Mirrors <c>LmKitApiFactory</c>'s hardening
/// (in-memory SQLite, no background workers, fake MCP client, disabled HTTPS/redis)
/// and additionally registers the document services + options in the TEST host — the
/// production Program.cs wiring is documented in PDF-INTEGRATION.md for the
/// coordinator, so these tests never depend on it.
/// </summary>
public abstract class DocumentsApiFactoryBase : WebApplicationFactory<Program>
{
    public static readonly Guid TenantId = Guid.Parse("77777777-7777-7777-7777-777777777777");
    public static readonly Guid UserId = Guid.Parse("88888888-8888-8888-8888-888888888888");
    public const string Email = "documents@example.test";
    public const string Password = "Documents-2026!";

    protected abstract bool DocumentToolsEnabled { get; }
    protected virtual long MaxInputBytes => 25L * 1024 * 1024;

    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly ServiceProvider _sqliteProvider = new ServiceCollection()
        .AddEntityFrameworkSqlite()
        .BuildServiceProvider();
    private readonly object _seedLock = new();
    private bool _seeded;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["JwtSettings:SecretKey"] = "integration-test-secret-key-at-least-32-bytes-long",
                ["JwtSettings:Issuer"] = "LmKitOmniApi",
                ["JwtSettings:Audience"] = "LmKitOmniClient",
                ["JwtSettings:ExpirationInMinutes"] = "30",
                ["AuthCookies:Secure"] = "false",
                ["HttpsRedirection:Enabled"] = "false",
                ["Database:ApplyMigrations"] = "false",
                ["BootstrapAdmin:Enabled"] = "false",
                ["ConnectionStrings:Redis"] = "",
                ["AiModels:WarmupChatModel"] = "false",
                ["AiModels:RequireChatModelReady"] = "false"
            });
        });
        builder.ConfigureServices(services =>
        {
            foreach (var descriptor in services
                .Where(service => service.ServiceType == typeof(IHostedService)
                    && service.ImplementationType is { } implementation
                    && (implementation == typeof(DocumentVectorizationWorker)
                        || implementation == typeof(DataRetentionWorker)
                        || implementation == typeof(ModelWarmupWorker)
                        || implementation == typeof(SchemaVectorizationWorker)))
                .ToList())
            {
                services.Remove(descriptor);
            }

            services.RemoveAll<HermesDbContext>();
            services.RemoveAll<DbContextOptions<HermesDbContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<HermesDbContext>>();
            _connection.Open();
            services.AddSingleton(_connection);
            services.AddDbContext<HermesDbContext>((provider, options) =>
                options.UseSqlite(_connection)
                    .UseInternalServiceProvider(_sqliteProvider)
                    .AddInterceptors(provider.GetRequiredService<AuditSaveChangesInterceptor>()));
            services.RemoveAll<IMcpProtocolClient>();
            services.AddSingleton<IMcpProtocolClient, TestMcpProtocolClient>();

            // ── Document tools registration (mirrors the Program.cs snippet in PDF-INTEGRATION.md) ──
            services.Configure<DocumentToolsOptions>(o =>
            {
                o.Enabled = DocumentToolsEnabled;
                o.MaxInputBytes = MaxInputBytes;
                o.MaxSearchTerms = 50;
                o.MaxOutputBytes = 25L * 1024 * 1024;
            });
            services.AddScoped<IPdfFormService, PdfFormService>();
            services.AddScoped<IDocumentRedactionService, DocumentRedactionService>();
        });
    }

    public void EnsureSeeded()
    {
        lock (_seedLock)
        {
            if (_seeded) return;
            using var scope = Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<HermesDbContext>();
            db.Database.EnsureCreated();

            db.Tenants.Add(new Tenant { Id = TenantId, Name = "Documents tenant" });
            db.Users.Add(new User
            {
                Id = UserId,
                TenantId = TenantId,
                Username = "documents",
                Email = Email,
                FullName = "Documents User",
                Role = "Admin",
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(Password)
            });
            db.SaveChanges();
            _seeded = true;
        }
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            _connection.Dispose();
            _sqliteProvider.Dispose();
        }
    }
}

/// <summary>Document tools enabled, default (25 MB) input cap.</summary>
public sealed class DocumentsEnabledFactory : DocumentsApiFactoryBase
{
    protected override bool DocumentToolsEnabled => true;
}

/// <summary>Document tools disabled — every endpoint must return 501.</summary>
public sealed class DocumentsDisabledFactory : DocumentsApiFactoryBase
{
    protected override bool DocumentToolsEnabled => false;
}

/// <summary>Document tools enabled but with a tiny (64-byte) input cap, to exercise the upload-size guard cheaply.</summary>
public sealed class DocumentsTinyLimitFactory : DocumentsApiFactoryBase
{
    protected override bool DocumentToolsEnabled => true;
    protected override long MaxInputBytes => 64;
}
