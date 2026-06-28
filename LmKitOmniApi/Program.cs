using Microsoft.EntityFrameworkCore;
using LmKitOmniApi.Infrastructure.Data;
using LmKitOmniApi.Infrastructure.VectorDb;
using LmKitOmniApi.Application.Abstractions;
using LmKitOmniApi.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using System.Text;
using LmKitOmniApi.Domain.Entities;
using LmKitOmniApi.Infrastructure.AI;
using LmKitOmniApi.Infrastructure.AI.Security;
using LmKitOmniApi.Infrastructure.AI.Filters;

var builder = WebApplication.CreateBuilder(args);

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

// Đăng ký CORS
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", builder =>
    {
        builder.WithOrigins("http://localhost:5173")
               .AllowCredentials()
               .AllowAnyMethod()
               .AllowAnyHeader();
    });
});

// Đăng ký SignalR
builder.Services.AddSignalR();

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
            }
        };
    });

// 1. Cấu hình DbContext (PostgreSQL) đọc từ AppSettings
builder.Services.AddDbContext<HermesDbContext>(options =>
    options.UseNpgsql(builder.Configuration["PostgreSql"]));

// Đăng ký Qdrant Vector DB
builder.Services.AddSingleton<IVectorStoreService, QdrantVectorService>(sp =>
{
    return new QdrantVectorService(builder.Configuration);
});

// ============================================================
// 🛡️ AI Safety & Security Services (Phase 1)
// ============================================================
builder.Services.AddScoped<IPromptGuardService, PromptGuardService>();
builder.Services.AddScoped<IToolPermissionService, ToolPermissionService>();
builder.Services.AddScoped<ToolSandboxService>();

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

var app = builder.Build();

// Đảm bảo Database luôn tồn tại và tạo mới nếu chưa có
using (var scope = app.Services.CreateScope())
{
    var dbContext = scope.ServiceProvider.GetRequiredService<LmKitOmniApi.Infrastructure.Data.HermesDbContext>();
    dbContext.Database.EnsureCreated();

    // Data Seeding
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
                Email = "admin@lmkit.net",
                PasswordHash = BCrypt.Net.BCrypt.HashPassword("admin"),
                FullName = "Admin User",
                Role = "Admin",
                TenantId = tenant.Id
            };
            dbContext.Users.Add(adminUser);
            dbContext.SaveChanges();
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
app.UseHttpsRedirection();

// Kích hoạt CORS
app.UseCors("AllowAll");

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

// Map SignalR Hub
app.MapHub<LmKitOmniApi.Infrastructure.Hubs.NotificationHub>("/hubs/notifications");

app.Run();
