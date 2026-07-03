using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.SignalR;
using LmKitOmniApi.Infrastructure.Data;
using LmKitOmniApi.Infrastructure.VectorDb;
using LmKitOmniApi.Application.Abstractions;
using LmKitOmniApi.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using System.Text;
using System.Security.Cryptography;
using System.Threading.RateLimiting;
using LmKitOmniApi.Domain.Entities;
using LmKitOmniApi.Infrastructure.AI;
using LmKitOmniApi.Infrastructure.AI.Security;
using LmKitOmniApi.Infrastructure.AI.Filters;
using Hangfire;
using Hangfire.PostgreSql;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Microsoft.Extensions.Caching.Distributed;
using LmKitOmniApi.Infrastructure.Notifications;
using LmKitOmniApi.Application.Jobs;
using LmKitOmniApi.Application.Chat;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, configuration) => 
    configuration.ReadFrom.Configuration(context.Configuration)
                 .WriteTo.Console());

// Khởi tạo LM-Kit.NET License
LMKit.Licensing.LicenseManager.SetLicenseKey("");

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

// Đăng ký SignalR với Redis Backplane
var signalRRedisConn = builder.Configuration.GetConnectionString("Redis");
if (!string.IsNullOrEmpty(signalRRedisConn))
{
    // builder.Services.AddSignalR().AddStackExchangeRedis(signalRRedisConn);
    builder.Services.AddSignalR(); // Tạm thời fallback do lỗi package của .NET 10 preview
}
else
{
    builder.Services.AddSignalR();
}

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
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwtSettings["Issuer"],
            ValidAudience = jwtSettings["Audience"],
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSettings["SecretKey"]!))
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
                    }
                }
            }
        };
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

// Cấu hình Hangfire
builder.Services.AddHangfire(configuration => configuration
    .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
    .UseSimpleAssemblyNameTypeSerializer()
    .UseRecommendedSerializerSettings()
    .UsePostgreSqlStorage(options => options.UseNpgsqlConnection(builder.Configuration["PostgreSql"])));

builder.Services.AddHangfireServer();

// Đăng ký Neo4j Driver
var neo4jUri = builder.Configuration["GraphDb:Uri"] ?? "bolt://localhost:7687";
var neo4jUser = builder.Configuration["GraphDb:Username"] ?? "neo4j";
var neo4jPassword = builder.Configuration["GraphDb:Password"] ?? "neo4j";
builder.Services.AddSingleton(Neo4j.Driver.GraphDatabase.Driver(neo4jUri, Neo4j.Driver.AuthTokens.Basic(neo4jUser, neo4jPassword)));

// Đăng ký Telegram Proactive Agent
builder.Services.Configure<TelegramSettings>(builder.Configuration.GetSection("TelegramSettings"));
builder.Services.AddScoped<ITelegramNotificationService, TelegramNotificationService>();

// ============================================================
// 🛡️ AI Safety & Security Services (Phase 1)
// ============================================================
builder.Services.AddScoped<IPromptGuardService, PromptGuardService>();
builder.Services.AddScoped<IToolPermissionService, ToolPermissionService>();
builder.Services.AddScoped<ToolSandboxService>();
builder.Services.AddScoped<IExecutionSandboxEngine, ExecutionSandboxEngine>();

// Filter Pipeline (ordered execution)
builder.Services.AddScoped<IAgentFilter, InputSanitizationFilter>();
builder.Services.AddScoped<IAgentFilter, OutputGuardrailFilter>();
builder.Services.AddScoped<AgentFilterPipeline>();

// ============================================================
// 🧠 Agent Memory & Token Management (Phase 2)
// ============================================================
builder.Services.AddScoped<IAgentMemoryService, AgentMemoryService>();
builder.Services.AddScoped<ITokenManagementService, TokenManagementService>();
builder.Services.AddScoped<IGraphKnowledgeService, GraphKnowledgeService>();
builder.Services.AddScoped<ISentimentAnalyzerService, SentimentAnalyzerService>();

// ============================================================
// 🔍 Query Expansion (Phase 4 — Hybrid Search)
// ============================================================
builder.Services.AddScoped<QueryExpansionService>();

// Đăng ký RAG Services (enhanced with Hybrid Search)
builder.Services.AddScoped<ITextChunkingService, TextChunkingService>();
builder.Services.AddScoped<IRagPipelineService, RagPipelineService>();

// Đăng ký Background Worker cho RAG Bất đồng bộ
builder.Services.AddHostedService<LmKitOmniApi.Infrastructure.Workers.DocumentVectorizationWorker>();

// ============================================================
// 🔄 Multi-Agent System (Phase 3)
// ============================================================
builder.Services.AddScoped<ISpecializedAgent, LmKitOmniApi.Infrastructure.AI.Agents.ResearchAgent>();
builder.Services.AddScoped<ISpecializedAgent, LmKitOmniApi.Infrastructure.AI.Agents.AnalysisAgent>();
builder.Services.AddScoped<ISpecializedAgent, LmKitOmniApi.Infrastructure.AI.Agents.VisionAgent>();
builder.Services.AddScoped<LmKitOmniApi.Infrastructure.AI.Agents.MultiAgentOrchestrator>();
builder.Services.AddScoped<DagWorkflowOrchestrator>();

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
builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService("LmKitOmniApi"))
    .WithMetrics(metrics => metrics
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddMeter("LmKitOmniApi.AgentMetrics")
        .AddPrometheusExporter())
    .WithTracing(tracing => tracing
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddSource("LmKitOmniApi.Agent"));

builder.Services.AddSingleton<LmKitOmniApi.Infrastructure.AI.Observability.AgentTelemetryService>();
builder.Services.AddSingleton<LmKitOmniApi.Infrastructure.AI.Resilience.AgentResiliencePolicy>();

// ============================================================
// 🔗 MCP Integration (Phase 7)
// ============================================================
builder.Services.AddHttpClient("MCP");
builder.Services.AddSingleton<LmKitOmniApi.Infrastructure.AI.Mcp.McpClientService>();

// ============================================================
// 📋 Skill Registry & Prompt Templates
// ============================================================
builder.Services.AddScoped<AgentSkillRegistry>();
builder.Services.AddSingleton<PromptTemplateEngine>();

// Đăng ký Agent Orchestrator (FULLY INTEGRATED — all services wired)
builder.Services.AddScoped<IAgentOrchestrator, AgentOrchestrator>();

// Đăng ký Advanced Tools
builder.Services.AddScoped<IWebSearchService, LmKitOmniApi.Infrastructure.Web.DuckDuckGoSearchService>();
builder.Services.AddScoped<IOfficeDocumentToolService, LmKitOmniApi.Infrastructure.Tools.OfficeDocumentToolService>();

// ============================================================
// 🏥 Health Checks
// ============================================================
builder.Services.AddHealthChecks();

// ============================================================
// 🚦 Rate Limiting (bảo vệ tài nguyên LLM đắt đỏ)
// ============================================================
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    
    // In Production with multiple instances, implement an IDistributedCache / Redis-backed sliding window here.
    // E.g. using AspNetCoreRateLimit or a custom middleware. Using local TokenBucket for now.
    options.AddPolicy("ai-agent", httpContext =>
        RateLimitPartition.GetTokenBucketLimiter(
            httpContext.User.Identity?.Name ?? httpContext.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
            _ => new TokenBucketRateLimiterOptions
            {
                TokenLimit = 10,
                ReplenishmentPeriod = TimeSpan.FromMinutes(1),
                TokensPerPeriod = 5,
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
});

var app = builder.Build();

// Database Migration - Bỏ Migrate() tự động trong code API để tránh lock table trên cluster
// Hãy chạy dotnet ef database update trong CI/CD hoặc Init Container
using (var scope = app.Services.CreateScope())
{
    var dbContext = scope.ServiceProvider.GetRequiredService<LmKitOmniApi.Infrastructure.Data.HermesDbContext>();
    // Data Seeding — tạo tài khoản admin với mật khẩu ngẫu nhiên an toàn
    if (!dbContext.Tenants.Any())
    {
        var tenant = new Tenant { Name = "Default Tenant" };
        dbContext.Tenants.Add(tenant);
        dbContext.SaveChanges();

        if (!dbContext.Users.Any())
        {
            // Sinh mật khẩu ngẫu nhiên 16 ký tự thay vì dùng "admin"
            var randomPassword = Convert.ToBase64String(RandomNumberGenerator.GetBytes(12));
            var adminUser = new User
            {
                Username = "admin",
                Email = "admin@lmkit.net",
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(randomPassword),
                FullName = "Admin User",
                Role = "Admin",
                TenantId = tenant.Id
            };
            dbContext.Users.Add(adminUser);
            dbContext.SaveChanges();

            // In mật khẩu ra console để admin biết — chỉ hiển thị 1 lần duy nhất
            Console.WriteLine("============================================");
            Console.WriteLine($"  ADMIN ACCOUNT CREATED");
            Console.WriteLine($"  Username: admin");
            Console.WriteLine($"  Password: {randomPassword}");
            Console.WriteLine($"  ⚠️  PLEASE CHANGE THIS PASSWORD IMMEDIATELY");
            Console.WriteLine("============================================");
        }
    }
}

// Cấu hình Hangfire Recurring Jobs (Proactive Agent & Model Distillation)
RecurringJob.AddOrUpdate<ProactiveMonitorJob>(
    "proactive-monitor-job",
    job => job.RunMonitorAsync(CancellationToken.None),
    "*/30 * * * *" // Chạy định kỳ mỗi 30 phút
);

RecurringJob.AddOrUpdate<ContinuousFineTuningJob>(
    "nightly-lora-finetuning-job",
    job => job.RunNightlyFineTuningAsync(CancellationToken.None),
    "0 2 * * *" // Chạy vào lúc 2:00 AM mỗi ngày
);

RecurringJob.AddOrUpdate<ReflexionJob>(
    "nightly-reflexion-job",
    job => job.RunReflexionAsync(CancellationToken.None),
    "30 2 * * *" // Chạy vào lúc 2:30 AM mỗi ngày
);

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
app.UseHttpsRedirection();

// Kích hoạt CORS (đã đổi tên policy từ "AllowAll" → "ProductionCors")
app.UseCors("ProductionCors");

// Kích hoạt Rate Limiting
app.UseRateLimiter();

app.UseAuthentication();
app.UseAuthorization();

app.UseHangfireDashboard("/hangfire", new DashboardOptions
{
    Authorization = new[] { new LmKitOmniApi.Infrastructure.Security.HangfireAuthorizationFilter() }
});

// Kích hoạt Prometheus Scrape Endpoint cho OpenTelemetry
app.UseOpenTelemetryPrometheusScrapingEndpoint();

app.MapControllers();

// Health Check endpoint
app.MapHealthChecks("/health");

// Map SignalR Hub
app.MapHub<LmKitOmniApi.Infrastructure.Hubs.NotificationHub>("/hubs/notifications");

app.Run();
