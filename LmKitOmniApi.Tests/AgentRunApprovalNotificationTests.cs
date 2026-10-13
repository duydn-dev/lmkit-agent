using LmKitOmniApi.Application.Abstractions;
using LmKitOmniApi.Application.AgentRuns;
using LmKitOmniApi.Application.AgentRuns.Commands;
using LmKitOmniApi.Application.AgentRuns.Handlers;
using LmKitOmniApi.Domain.Entities;
using LmKitOmniApi.Infrastructure.AI;
using LmKitOmniApi.Infrastructure.Data;
using LmKitOmniApi.Services;
using LMKit.TextGeneration.Chat;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using System.Runtime.CompilerServices;

namespace LmKitOmniApi.Tests;

/// <summary>
/// Run TƯƠNG TÁC chạm HITL trong lúc user đã rời màn hình phải để lại Notification
/// kèm deep-link AgentRunId (chuông web/mobile mở thẳng gate card Duyệt/Từ chối).
/// Run của LỊCH (ScheduledTaskId != null) KHÔNG tạo ở đây — ScheduledTaskWorker đã
/// tạo bản riêng, tránh spam 2 thông báo cho cùng một lần dừng.
/// </summary>
public sealed class AgentRunApprovalNotificationTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly Guid UserId = Guid.NewGuid();

    public AgentRunApprovalNotificationTests()
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
        db.SaveChanges();
    }

    public void Dispose() => _connection.Dispose();

    private HermesDbContext CreateContext()
        => new(new DbContextOptionsBuilder<HermesDbContext>().UseSqlite(_connection).Options);

    private sealed class GatingOrchestrator : IAgentOrchestrator
    {
        public Task<string> ExecuteDirectActionAsync(
            Guid tenantId, Guid userId, string action, string query, Guid? approvalId = null, CancellationToken ct = default)
            => Task.FromResult("ok");

        public async IAsyncEnumerable<string> StreamProcessQueryAsync(
            Guid tenantId, Guid sessionId, Guid userId, string userRole, string query,
            ChatHistory history, AgentRequestOptions? options,
            [EnumeratorCancellation] CancellationToken cancellationToken,
            IList<AgentRunStepData>? stepSink = null)
        {
            await Task.Yield();
            yield return $"[HITL_APPROVAL_REQUIRED:{Guid.NewGuid()}] Cần duyệt ghi CSDL";
        }
    }

    private sealed class StubHistoryFactory : IAgentRunHistoryFactory
    {
        public Task<ChatHistory> CreateAsync(CancellationToken ct) => Task.FromResult(new ChatHistory(null!));
    }

    private async Task<Guid> DriveAsync(Guid? scheduledTaskId)
    {
        await using var db = CreateContext();
        using var modelManager = new LmModelManager(new ConfigurationBuilder().Build());
        var handler = new StreamAgentRunCommandHandler(
            new GatingOrchestrator(), db, new StubHistoryFactory(),
            new TokenManagementService(modelManager, NullLogger<TokenManagementService>.Instance),
            modelManager);
        var command = new StreamAgentRunCommand
        {
            TenantId = TenantId,
            UserId = UserId,
            Goal = "Xóa đơn nháp hôm qua",
            ScheduledTaskId = scheduledTaskId
        };
        await foreach (var _ in handler.Handle(command, CancellationToken.None)) { }
        return command.RunId;
    }

    [Fact]
    public async Task InteractiveRun_ParkedOnApproval_LeavesOneDeepLinkedNotification()
    {
        var runId = await DriveAsync(scheduledTaskId: null);

        await using var db = CreateContext();
        var notification = Assert.Single(db.Notifications.Where(n =>
            n.TenantId == TenantId && n.UserId == UserId));
        Assert.Equal("agent_approval_required", notification.Type);
        Assert.Equal(runId, notification.AgentRunId);
        Assert.False(notification.IsRead);
    }

    [Fact]
    public async Task ScheduledRun_ParkedOnApproval_LeavesNoNotificationHere()
    {
        await DriveAsync(scheduledTaskId: Guid.NewGuid());

        await using var db = CreateContext();
        Assert.Empty(db.Notifications);
    }
}
