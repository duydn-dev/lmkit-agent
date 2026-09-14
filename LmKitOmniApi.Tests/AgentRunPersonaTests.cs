using System.Runtime.CompilerServices;
using LmKitOmniApi.Application.Abstractions;
using LmKitOmniApi.Application.AgentRuns;
using LmKitOmniApi.Application.AgentRuns.Commands;
using LmKitOmniApi.Application.AgentRuns.Handlers;
using LmKitOmniApi.Domain.Entities;
using LmKitOmniApi.Infrastructure.AI;
using LmKitOmniApi.Infrastructure.Data;
using LMKit.TextGeneration.Chat;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace LmKitOmniApi.Tests;

/// <summary>
/// Persona trên agent run (StreamAgentRunCommand.CustomAgentId): agent truy cập
/// được → orchestrator nhận persona + tool whitelist + knowledge + LoRA của
/// agent; agent lạ/không quyền → chạy KHÔNG persona (soft reference) thay vì gãy.
/// Đây chính là đường lịch chế độ agent đi qua (worker chỉ đặt CustomAgentId).
/// </summary>
public sealed class AgentRunPersonaTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly Guid StrangerId = Guid.NewGuid();

    public AgentRunPersonaTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        using var db = CreateContext();
        db.Database.EnsureCreated();
        db.Tenants.Add(new Tenant { Id = TenantId, Name = "T" });
        db.Users.Add(new User
        {
            Id = UserId, TenantId = TenantId, Username = "u", Email = "u@x.test",
            PasswordHash = "h", FullName = "U", Role = "Member", IsActive = true
        });
        db.Users.Add(new User
        {
            Id = StrangerId, TenantId = TenantId, Username = "s", Email = "s@x.test",
            PasswordHash = "h", FullName = "S", Role = "Member", IsActive = true
        });
        db.SaveChanges();
    }

    public void Dispose() => _connection.Dispose();

    private HermesDbContext CreateContext()
        => new(new DbContextOptionsBuilder<HermesDbContext>().UseSqlite(_connection).Options);

    private sealed class OptionsCapturingOrchestrator : IAgentOrchestrator
    {
        public readonly List<AgentRequestOptions?> CapturedOptions = [];

        public Task<string> ExecuteDirectActionAsync(
            Guid tenantId, Guid userId, string action, string query, Guid? approvalId = null, CancellationToken ct = default)
            => Task.FromResult("ok");

        public async IAsyncEnumerable<string> StreamProcessQueryAsync(
            Guid tenantId, Guid sessionId, Guid userId, string userRole, string query,
            ChatHistory history, AgentRequestOptions? options,
            [EnumeratorCancellation] CancellationToken cancellationToken,
            IList<AgentRunStepData>? stepSink = null)
        {
            CapturedOptions.Add(options);
            await Task.Yield();
            yield return "Xong.";
        }
    }

    private sealed class StubHistoryFactory : IAgentRunHistoryFactory
    {
        public Task<ChatHistory> CreateAsync(CancellationToken ct) => Task.FromResult(new ChatHistory(null!));
    }

    private async Task<AgentRequestOptions?> DriveAsync(Guid? customAgentId)
    {
        await using var db = CreateContext();
        var orchestrator = new OptionsCapturingOrchestrator();
        var handler = new StreamAgentRunCommandHandler(orchestrator, db, new StubHistoryFactory());
        var command = new StreamAgentRunCommand
        {
            TenantId = TenantId,
            UserId = UserId,
            Goal = "Đếm đơn hàng hôm qua",
            CustomAgentId = customAgentId
        };
        await foreach (var _ in handler.Handle(command, CancellationToken.None)) { }
        return Assert.Single(orchestrator.CapturedOptions);
    }

    [Fact]
    public async Task AccessibleAgent_FlowsPersonaToolsKnowledgeAndLoraIntoTheRun()
    {
        var loraId = Guid.NewGuid();
        var documentId = Guid.NewGuid();
        Guid agentId;
        await using (var db = CreateContext())
        {
            var agent = new CustomAgent
            {
                TenantId = TenantId,
                OwnerUserId = UserId,
                Name = "Chuyên viên báo cáo",
                PersonaPrompt = "Bạn là chuyên viên báo cáo môi trường.",
                AllowedToolsCsv = "DbQuery,AnalyzeText",
                KnowledgeDocumentIdsCsv = documentId.ToString(),
                LoraAdapterId = loraId
            };
            db.CustomAgents.Add(agent);
            await db.SaveChangesAsync();
            agentId = agent.Id;
        }

        var options = await DriveAsync(agentId);

        Assert.NotNull(options);
        Assert.Equal("Bạn là chuyên viên báo cáo môi trường.", options!.PersonaPrompt);
        Assert.Equal(new[] { "DbQuery", "AnalyzeText" }, options.AllowedTools);
        Assert.Equal(new[] { documentId }, options.KnowledgeDocumentIds);
        Assert.Equal(loraId, options.LoraAdapterId);
        // Whitelist không chứa SearchWeb → web search bị tắt cho run này.
        Assert.False(options.AllowWebSearch);
    }

    [Fact]
    public async Task SharedAgentOfAnotherUser_IsUsable()
    {
        Guid agentId;
        await using (var db = CreateContext())
        {
            var agent = new CustomAgent
            {
                TenantId = TenantId,
                OwnerUserId = StrangerId,
                Name = "Agent chia sẻ",
                PersonaPrompt = "Persona dùng chung.",
                IsSharedWithTenant = true
            };
            db.CustomAgents.Add(agent);
            await db.SaveChangesAsync();
            agentId = agent.Id;
        }

        var options = await DriveAsync(agentId);
        Assert.Equal("Persona dùng chung.", options?.PersonaPrompt);
        // Không whitelist → mọi tool theo quyền, web search mở.
        Assert.True(options!.AllowWebSearch);
        Assert.Null(options.AllowedTools);
    }

    [Fact]
    public async Task PrivateAgentOfAnotherUser_RunsWithoutPersona_NotAnError()
    {
        Guid agentId;
        await using (var db = CreateContext())
        {
            var agent = new CustomAgent
            {
                TenantId = TenantId,
                OwnerUserId = StrangerId,
                Name = "Agent riêng tư",
                PersonaPrompt = "Bí mật của người khác.",
                IsSharedWithTenant = false
            };
            db.CustomAgents.Add(agent);
            await db.SaveChangesAsync();
            agentId = agent.Id;
        }

        var options = await DriveAsync(agentId);
        Assert.Null(options); // soft reference: không persona, không lộ prompt người khác
    }

    [Fact]
    public async Task MissingAgent_RunsWithoutPersona()
    {
        var options = await DriveAsync(Guid.NewGuid());
        Assert.Null(options);
    }
}
