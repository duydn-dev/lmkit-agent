using Microsoft.EntityFrameworkCore;
using LmKitOmniApi.Infrastructure.Data;
using LmKitOmniApi.Infrastructure.VectorDb;
using LmKitOmniApi.Application.Abstractions;
using LmKitOmniApi.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.IdentityModel.Tokens;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Security.Claims;
using System.Threading.RateLimiting;
using LmKitOmniApi.Domain.Entities;
using LmKitOmniApi.Infrastructure.AI;
using LmKitOmniApi.Infrastructure.AI.Security;
using LmKitOmniApi.Infrastructure.AI.Filters;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Microsoft.Extensions.Caching.Distributed;
using LmKitOmniApi.Application.Chat;
using LmKitOmniApi.Infrastructure.AI.Tools;
using LmKitOmniApi.Infrastructure.Security;
using Microsoft.AspNetCore.DataProtection;
using System.Security.Cryptography.X509Certificates;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, configuration) => 
    configuration.ReadFrom.Configuration(context.Configuration)
                 .WriteTo.Console());

// Khởi tạo LM-Kit.NET License
var lmKitLicenseKey = builder.Configuration["LMKit:LicenseKey"];
if (!string.IsNullOrWhiteSpace(lmKitLicenseKey))
    LMKit.Licensing.LicenseManager.SetLicenseKey(lmKitLicenseKey);

// Cấu hình giới hạn kích thước upload lớn
builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = 100 * 1024 * 1024; // 100 MB
});

// Đăng ký LmModelManager như một Singleton
builder.Services.AddSingleton<LmModelManager>();

// Đăng ký ProblemDetails & GlobalExceptionHandler
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<LmKitOmniApi.Infrastructure.Exceptions.GlobalExceptionHandler>();

builder.Services.AddControllers();

var dataProtectionKeyPath = builder.Configuration["DataProtection:KeyPath"]
    ?? Path.Combine(builder.Environment.ContentRootPath, "App_Data", "DataProtectionKeys");
Directory.CreateDirectory(dataProtectionKeyPath);
var dataProtection = builder.Services.AddDataProtection()
    .SetApplicationName("LmKitOmniApi")
    .PersistKeysToFileSystem(new DirectoryInfo(dataProtectionKeyPath));
var dataProtectionCertificatePath = builder.Configuration["DataProtection:CertificatePath"];
if (!string.IsNullOrWhiteSpace(dataProtectionCertificatePath))
{
    var certificate = X509CertificateLoader.LoadPkcs12FromFile(
        dataProtectionCertificatePath,
        builder.Configuration["DataProtection:CertificatePassword"]);
    dataProtection.ProtectKeysWithCertificate(certificate);
}
builder.Services.AddSingleton<TaskApprovalPayloadProtector>();
builder.Services.AddSingleton<McpHeaderProtector>();

// Đăng ký CORS (đọc origins từ cấu hình, không hardcode)
builder.Services.AddCors(options =>
{
    options.AddPolicy("ProductionCors", policy =>
    {
        var origins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
                      ?? new[] { "http://localhost:5173" };
        policy.WithOrigins(origins)
              .AllowCredentials()
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

// Đăng ký Swagger/OpenAPI
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Đăng ký MediatR
builder.Services.AddMediatR(cfg => cfg.RegisterServicesFromAssembly(typeof(Program).Assembly));

// Đăng ký Authentication & JWT
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        var jwtSettings = builder.Configuration.GetSection("JwtSettings");
        var jwtSecret = jwtSettings["SecretKey"];
        if (string.IsNullOrWhiteSpace(jwtSecret) || Encoding.UTF8.GetByteCount(jwtSecret) < 32)
            throw new InvalidOperationException("JwtSettings:SecretKey must be configured with at least 32 bytes.");

        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwtSettings["Issuer"],
            ValidAudience = jwtSettings["Audience"],
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSecret)),
            NameClaimType = ClaimTypes.NameIdentifier,
            RoleClaimType = "Role"
        };
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                if (context.Request.Cookies.ContainsKey("hermes_token"))
                {
                    context.Token = context.Request.Cookies["hermes_token"];
                }
                return Task.CompletedTask;
            },
            OnTokenValidated = async context =>
            {
                var cache = context.HttpContext.RequestServices.GetRequiredService<IDistributedCache>();
                var tokenStr = context.Principal?.FindFirst("jti")?.Value ?? context.SecurityToken.Id;
                if (!string.IsNullOrEmpty(tokenStr))
                {
                    var isBlacklisted = await cache.GetStringAsync($"blacklist_{tokenStr}");
                    if (!string.IsNullOrEmpty(isBlacklisted))
                    {
                        context.Fail("Token has been revoked");
                        return;
                    }
                }

                if (!Guid.TryParse(context.Principal?.FindFirstValue(ClaimTypes.NameIdentifier), out var userId)
                    || !Guid.TryParse(context.Principal?.FindFirstValue("sid"), out var sessionId)
                    || !Guid.TryParse(context.Principal?.FindFirstValue("TenantId"), out var tenantId))
                {
                    context.Fail("Token session claims are invalid");
                    return;
                }

                var authDb = context.HttpContext.RequestServices.GetRequiredService<HermesDbContext>();
                var sessionIsActive = await authDb.UserSessions.AnyAsync(session =>
                    session.Id == sessionId
                    && session.UserId == userId
                    && session.Status == "active"
                    && session.ExpiresAtUtc > DateTime.UtcNow
                    && session.User != null
                    && session.User.IsActive
                    && session.User.TenantId == tenantId,
                    context.HttpContext.RequestAborted);
                if (!sessionIsActive) context.Fail("Session is inactive or revoked");
            }
        };

        // Requests presenting X-Api-Key are handed to the ApiKey scheme for every
        // default authenticate/challenge, so HttpContext.User is populated from the
        // key even on surfaces that bypass endpoint policies (the /metrics admin
        // gate, [Authorize(Roles = ...)] policies without explicit schemes, and the
        // rate-limiter partitions). Requests WITHOUT the header keep the exact
        // JWT pipeline above — cookie extraction, OnTokenValidated session
        // revocation — untouched. When both credentials are present the explicit
        // API key wins by design.
        options.ForwardDefaultSelector = context =>
            context.Request.Headers.TryGetValue(ApiKeyAuthenticationHandler.HeaderName, out var apiKeyHeader)
                && !string.IsNullOrWhiteSpace(apiKeyHeader)
                ? ApiKeyAuthenticationHandler.SchemeName
                : null;
    })
    // Programmatic access: X-Api-Key header validated against hashed TenantApiKeys.
    .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(
        ApiKeyAuthenticationHandler.SchemeName, null);

// Plain [Authorize] must accept BOTH schemes: the default policy authenticates the
// JWT cookie/bearer scheme AND the ApiKey scheme and merges their principals. The
// JWT scheme stays the default authenticate/challenge scheme, so anonymous requests
// still get the same 401 challenge as before.
builder.Services.AddAuthorization(options =>
{
    options.DefaultPolicy = new AuthorizationPolicyBuilder(
            JwtBearerDefaults.AuthenticationScheme,
            ApiKeyAuthenticationHandler.SchemeName)
        .RequireAuthenticatedUser()
        .Build();
});

// 1. Cấu hình DbContext (PostgreSQL) đọc từ AppSettings
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<LmKitOmniApi.Infrastructure.Data.Interceptors.AuditSaveChangesInterceptor>();
builder.Services.AddDbContext<HermesDbContext>((sp, options) =>
{
    var interceptor = sp.GetRequiredService<LmKitOmniApi.Infrastructure.Data.Interceptors.AuditSaveChangesInterceptor>();
    options.UseNpgsql(builder.Configuration["PostgreSql"], npgsqlOptions => 
            npgsqlOptions.EnableRetryOnFailure(maxRetryCount: 3))
           .AddInterceptors(interceptor);
});

// Đăng ký Qdrant Vector DB
builder.Services.AddSingleton<IVectorStoreService, QdrantVectorService>();

// ============================================================
// 🛡️ AI Safety & Security Services (Phase 1)
// ============================================================
builder.Services.AddScoped<IPromptGuardService, PromptGuardService>();
// The rate window must survive individual HTTP request scopes.
builder.Services.AddSingleton<IToolPermissionService, ToolPermissionService>();
builder.Services.AddScoped<ToolSandboxService>();
builder.Services.AddScoped<UserResourceAccessService>();
builder.Services.AddScoped<IExecutionSandboxEngine, ExecutionSandboxEngine>();
builder.Services.AddScoped<AgentToolGateway>();

// Filter Pipeline (ordered execution)
builder.Services.AddScoped<IAgentFilter, InputSanitizationFilter>();
builder.Services.AddScoped<IAgentFilter, OutputGuardrailFilter>();
builder.Services.AddScoped<AgentFilterPipeline>();

// ============================================================
// 🧠 Agent Memory & Token Management (Phase 2)
// ============================================================
builder.Services.AddScoped<IAgentMemoryService, AgentMemoryService>();
builder.Services.AddScoped<ITokenManagementService, TokenManagementService>();

// ============================================================
// 🔍 Query Expansion (Phase 4 — Hybrid Search)
// ============================================================
builder.Services.AddScoped<QueryExpansionService>();

// Đăng ký RAG Services (enhanced with Hybrid Search)
builder.Services.AddScoped<ITextChunkingService, TextChunkingService>();
builder.Services.AddScoped<IRagPipelineService, RagPipelineService>();

// Đăng ký Background Worker cho RAG Bất đồng bộ
builder.Services.AddHostedService<LmKitOmniApi.Infrastructure.Workers.DocumentVectorizationWorker>();
builder.Services.AddHostedService<LmKitOmniApi.Infrastructure.Workers.DataRetentionWorker>();
builder.Services.AddHostedService<LmKitOmniApi.Infrastructure.Workers.ModelWarmupWorker>();
// Tier 2: user-defined recurring prompts delivered as notifications.
builder.Services.AddHostedService<LmKitOmniApi.Infrastructure.Workers.ScheduledTaskWorker>();

// ============================================================
// 🔄 Multi-Agent System (Phase 3)
// ============================================================
builder.Services.AddScoped<ISpecializedAgent, LmKitOmniApi.Infrastructure.AI.Agents.ResearchAgent>();
builder.Services.AddScoped<ISpecializedAgent, LmKitOmniApi.Infrastructure.AI.Agents.AnalysisAgent>();
builder.Services.AddScoped<ISpecializedAgent, LmKitOmniApi.Infrastructure.AI.Agents.VisionAgent>();
builder.Services.AddScoped<LmKitOmniApi.Infrastructure.AI.Agents.MultiAgentOrchestrator>();

// ============================================================
// 📎 Chat + File Attachment (Phase 5)
// ============================================================
builder.Services.AddScoped<OCRKnowledgeIngestionService>();

// ============================================================
// 📊 Observability & Resilience (Phase 6)
// ============================================================

// 1. Cấu hình IDistributedCache (Redis hoặc In-Memory)
var redisConnectionString = builder.Configuration.GetConnectionString("Redis");
if (!string.IsNullOrEmpty(redisConnectionString))
{
    builder.Services.AddSingleton<StackExchange.Redis.IConnectionMultiplexer>(
        _ => StackExchange.Redis.ConnectionMultiplexer.Connect(redisConnectionString));
    builder.Services.AddStackExchangeRedisCache(options =>
    {
        options.Configuration = redisConnectionString;
        options.InstanceName = "LmKitOmniApi_";
    });
}
else
{
    builder.Services.AddDistributedMemoryCache();
}

// 2. Cấu hình OpenTelemetry
var otlpEnabled = !string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);
builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService("LmKitOmniApi"))
    .WithMetrics(metrics =>
    {
        metrics.AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            .AddMeter("LmKitOmniApi.AgentMetrics")
            .AddPrometheusExporter();
        if (otlpEnabled) metrics.AddOtlpExporter();
    })
    .WithTracing(tracing =>
    {
        tracing.AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            .AddSource("LmKitOmniApi.Agent");
        if (otlpEnabled) tracing.AddOtlpExporter();
    });

builder.Services.AddSingleton<LmKitOmniApi.Infrastructure.AI.Observability.AgentTelemetryService>();
builder.Services.AddScoped<LmKitOmniApi.Infrastructure.AI.Observability.AgentToolAuditService>();
builder.Services.AddSingleton<LmKitOmniApi.Infrastructure.AI.Resilience.AgentResiliencePolicy>();

// ============================================================
// 🔗 MCP Integration (Phase 7)
// ============================================================
builder.Services.AddHttpClient("MCP", client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
    client.MaxResponseContentBufferSize = 1_048_576;
}).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
{
    // Redirect targets have not passed the MCP URL/DNS sandbox checks.
    AllowAutoRedirect = false,
    // DNS-rebinding TOCTOU fix: the pre-invocation URL check resolves DNS once, and a
    // plain handler would resolve it AGAIN for the actual request — a malicious
    // resolver can answer with a public IP during validation and rebind to
    // 169.254.169.254 / RFC1918 space for the connection. The shared factory
    // re-resolves, re-vets every address with the sandbox's authoritative
    // classifier, and connects only to a vetted public IP (TLS still validates
    // against the original hostname via SNI). See SsrfSafeConnect.
    ConnectCallback = SsrfSafeConnect.CreateVettedConnectCallback()
});
builder.Services.AddScoped<LmKitOmniApi.Infrastructure.AI.Mcp.IMcpProtocolClient, LmKitOmniApi.Infrastructure.AI.Mcp.McpProtocolClient>();
builder.Services.AddScoped<LmKitOmniApi.Infrastructure.AI.Mcp.McpClientService>();

// ============================================================
// 🔬 Deep Research (Tier 2)
// ============================================================
// Scoped: wraps the scoped ToolSandboxService SSRF gate.
builder.Services.AddScoped<LmKitOmniApi.Infrastructure.AI.Research.IResearchUrlValidator,
    LmKitOmniApi.Infrastructure.AI.Research.SandboxResearchUrlValidator>();
builder.Services.AddHttpClient<LmKitOmniApi.Infrastructure.AI.Research.ResearchContentFetcher>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(15);
    client.MaxResponseContentBufferSize = LmKitOmniApi.Infrastructure.AI.Research.ResearchLimits.MaxContentBytes;
    client.DefaultRequestHeaders.UserAgent.ParseAdd("LmKitOmniAgent/1.0");
}).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
{
    // Redirect targets have not passed the research URL/DNS sandbox checks; the
    // fetcher treats 3xx as skip. ValidateUrlAsync re-vets DNS per fetch.
    AllowAutoRedirect = false,
    // Same DNS-rebinding TOCTOU defense as the MCP handler: ValidateUrlAsync
    // resolves DNS during validation, and without this the handler would resolve
    // AGAIN at connect time — a rebinding resolver could then point the socket at
    // internal/metadata IPs. Re-vet every resolved address and connect only to a
    // vetted public IP. See SsrfSafeConnect.
    ConnectCallback = SsrfSafeConnect.CreateVettedConnectCallback()
});
builder.Services.AddScoped<LmKitOmniApi.Infrastructure.AI.Research.DeepResearchService>();

// ============================================================
// 📋 Skill Registry & Prompt Templates
// ============================================================
builder.Services.AddSingleton<PromptTemplateEngine>();
builder.Services.AddSingleton<LmKitDefaultToolCatalog>();

// Đăng ký Agent Orchestrator (FULLY INTEGRATED — all services wired)
builder.Services.AddScoped<IAgentOrchestrator, AgentOrchestrator>();

// Đăng ký Advanced Tools
builder.Services.AddHttpClient<IWebSearchService, LmKitOmniApi.Infrastructure.Web.DuckDuckGoSearchService>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(10);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("LmKitOmniAgent/1.0");
});

// ============================================================
// 🏥 Health Checks
// ============================================================
var healthChecks = builder.Services.AddHealthChecks()
    .AddCheck<LmKitOmniApi.Infrastructure.Health.PostgresHealthCheck>("postgres", tags: ["ready"])
    .AddCheck<LmKitOmniApi.Infrastructure.Health.QdrantHealthCheck>("qdrant", tags: ["ready"])
    .AddCheck<LmKitOmniApi.Infrastructure.Health.LmKitModelHealthCheck>("lmkit-model", tags: ["ready"]);
if (!string.IsNullOrWhiteSpace(redisConnectionString))
    healthChecks.AddCheck<LmKitOmniApi.Infrastructure.Health.RedisHealthCheck>("redis", tags: ["ready"]);

// ============================================================
// 🚦 Rate Limiting (bảo vệ tài nguyên LLM đắt đỏ)
// ============================================================
var aiRequestsPerWindow = builder.Configuration.GetValue("RateLimiting:AiRequestsPerWindow", 10);
var aiWindowSeconds = builder.Configuration.GetValue("RateLimiting:AiWindowSeconds", 60);
if (aiRequestsPerWindow <= 0 || aiWindowSeconds <= 0)
    throw new InvalidOperationException("AI rate-limit values must be greater than zero.");

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = async (rejection, ct) =>
    {
        var retryAfterSeconds = rejection.Lease.TryGetMetadata(
            MetadataName.RetryAfter,
            out var retryAfter)
            ? Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds))
            : aiWindowSeconds;
        rejection.HttpContext.Response.Headers.RetryAfter = retryAfterSeconds.ToString();
        await rejection.HttpContext.Response.WriteAsJsonAsync(new
        {
            title = "Too many requests.",
            status = StatusCodes.Status429TooManyRequests
        }, ct);
    };
    
    // Local token bucket is the fallback and also protects each process. When Redis
    // is configured, DistributedAiRateLimitMiddleware adds a cross-replica atomic window.
    options.AddPolicy("ai-agent", httpContext =>
        RateLimitPartition.GetTokenBucketLimiter(
            httpContext.User.Identity?.Name ?? httpContext.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
            _ => new TokenBucketRateLimiterOptions
            {
                TokenLimit = aiRequestsPerWindow,
                ReplenishmentPeriod = TimeSpan.FromSeconds(aiWindowSeconds),
                TokensPerPeriod = aiRequestsPerWindow,
                AutoReplenishment = true
            }));

    options.AddPolicy("LoginPolicy", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            httpContext.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 5,
                Window = TimeSpan.FromSeconds(10),
                QueueProcessingOrder = System.Threading.RateLimiting.QueueProcessingOrder.OldestFirst,
                QueueLimit = 0
            }));

    // Anonymous share-link reads: per-IP fixed window so a single host cannot scan
    // for tokens, while legitimate viewers stay comfortably under the limit.
    options.AddPolicy("SharePolicy", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            httpContext.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 30,
                Window = TimeSpan.FromSeconds(60),
                QueueProcessingOrder = System.Threading.RateLimiting.QueueProcessingOrder.OldestFirst,
                QueueLimit = 0
            }));
});

var app = builder.Build();

if (builder.Configuration.GetValue<bool>("Database:ApplyMigrations"))
{
    using var migrationScope = app.Services.CreateScope();
    var migrationDb = migrationScope.ServiceProvider.GetRequiredService<HermesDbContext>();
    app.Logger.LogInformation("Applying database migrations before accepting traffic.");
    migrationDb.Database.Migrate();
}

// Bootstrap is explicit. Production must provision the first administrator via
// secret-backed configuration or an external identity workflow.
if (builder.Configuration.GetValue<bool>("BootstrapAdmin:Enabled"))
{
    var bootstrapEmail = builder.Configuration["BootstrapAdmin:Email"];
    var bootstrapPassword = builder.Configuration["BootstrapAdmin:Password"];
    if (string.IsNullOrWhiteSpace(bootstrapEmail) || string.IsNullOrWhiteSpace(bootstrapPassword))
        throw new InvalidOperationException("BootstrapAdmin is enabled but Email/Password is missing.");

    using var scope = app.Services.CreateScope();
    var dbContext = scope.ServiceProvider.GetRequiredService<LmKitOmniApi.Infrastructure.Data.HermesDbContext>();
    if (!dbContext.Tenants.Any())
    {
        var tenant = new Tenant { Name = "Default Tenant" };
        dbContext.Tenants.Add(tenant);
        dbContext.SaveChanges();

        if (!dbContext.Users.Any())
        {
            var adminUser = new User
            {
                Username = "admin",
                Email = bootstrapEmail,
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(bootstrapPassword),
                FullName = "Admin User",
                Role = "Admin",
                TenantId = tenant.Id
            };
            dbContext.Users.Add(adminUser);
            dbContext.SaveChanges();
            app.Logger.LogWarning("Bootstrap administrator {Email} was created; disable BootstrapAdmin immediately.", bootstrapEmail);
        }
    }
}

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(options =>
    {
        options.SwaggerEndpoint("/swagger/v1/swagger.json", "LmKit Omni API v1");
        options.RoutePrefix = string.Empty; 
    });
}

app.UseExceptionHandler();
if (builder.Configuration.GetValue("HttpsRedirection:Enabled", true))
{
    if (!app.Environment.IsDevelopment()) app.UseHsts();
    app.UseHttpsRedirection();
}

// Kích hoạt CORS (đã đổi tên policy từ "AllowAll" → "ProductionCors")
app.UseCors("ProductionCors");

app.UseRouting();
app.UseAuthentication();
// Authentication must run first so rate-limit partitions use the stable user id
// instead of grouping every signed-in caller behind the same proxy IP.
app.UseMiddleware<LmKitOmniApi.Infrastructure.Security.DistributedAiRateLimitMiddleware>();
app.UseRateLimiter();
app.UseAuthorization();

// Kích hoạt Prometheus Scrape Endpoint cho OpenTelemetry
app.UseWhen(
    context => context.Request.Path.Equals("/metrics", StringComparison.OrdinalIgnoreCase),
    metricsApp => metricsApp.Use(async (context, next) =>
    {
        if (context.User.Identity?.IsAuthenticated != true || !context.User.IsInRole("Admin"))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }
        await next(context);
    }));
app.UseOpenTelemetryPrometheusScrapingEndpoint();

app.MapControllers();

// Health Check endpoint
app.MapHealthChecks("/health");
app.MapHealthChecks("/health/live", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = _ => false
});
app.MapHealthChecks("/health/ready", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready")
});

app.Run();

// Exposes the top-level application entry point to WebApplicationFactory without
// changing production startup behavior.
public partial class Program;
