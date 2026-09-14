using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LmKitOmniApi.Application.AgentRuns;
using LmKitOmniApi.Application.AgentRuns.Handlers;
using LmKitOmniApi.Domain.Entities;
using LmKitOmniApi.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace LmKitOmniApi.Tests;

/// <summary>
/// POST /api/agent-runs/{id}/cancel: hủy được run đang ĐỖ (chờ phê duyệt — kèm
/// hủy approval treo; chờ lượt resume; mồ côi quá 15'), 409 cho run đã kết thúc
/// hoặc đang stream sống, 404 cho run không thuộc người gọi. GET list giờ trả
/// shape phân trang chuẩn.
/// </summary>
[Collection("DbSqlite")]
public sealed class CancelAgentRunApiTests : IClassFixture<LmKitApiFactory>
{
    private static readonly SemaphoreSlim ClientGate = new(1, 1);
    private static HttpClient? _client;

    private readonly LmKitApiFactory _factory;

    public CancelAgentRunApiTests(LmKitApiFactory factory)
    {
        _factory = factory;
        _factory.EnsureSeeded();
    }

    private async Task<(Guid RunId, Guid SessionId)> SeedRunAsync(
        string status, string? resumeState = null, DateTime? createdAtUtc = null,
        DateTime? completedAtUtc = null, bool withPendingApproval = false, Guid? userId = null)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HermesDbContext>();
        var session = new ChatSession
        {
            TenantId = LmKitApiFactory.TenantId,
            UserId = userId ?? LmKitApiFactory.UserId,
            Title = "run",
            IsAgentRun = true
        };
        var run = new AgentRun
        {
            TenantId = LmKitApiFactory.TenantId,
            UserId = userId ?? LmKitApiFactory.UserId,
            ChatSessionId = session.Id,
            Goal = $"muc tieu {Guid.NewGuid():N}",
            Status = status,
            ResumeState = resumeState,
            CreatedAtUtc = createdAtUtc ?? DateTime.UtcNow,
            CompletedAtUtc = completedAtUtc
        };
        db.Add(session);
        db.Add(run);
        if (withPendingApproval)
        {
            db.TaskApprovals.Add(new TaskApproval
            {
                TenantId = LmKitApiFactory.TenantId,
                UserId = userId ?? LmKitApiFactory.UserId,
                ChatSessionId = session.Id,
                ActionName = "DBWRITE",
                ParametersJson = "{}"
            });
        }
        await db.SaveChangesAsync();
        return (run.Id, session.Id);
    }

    private async Task<(string Status, string? Error)> ReadRunAsync(Guid runId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HermesDbContext>();
        var run = await Task.FromResult(db.AgentRuns.Single(r => r.Id == runId));
        return (run.Status, run.Error);
    }

    [Fact]
    public async Task Cancel_AwaitingApprovalRun_ClosesRun_AndCancelsPendingApproval()
    {
        var (runId, sessionId) = await SeedRunAsync(
            AgentRunStatuses.AwaitingApproval, withPendingApproval: true);
        var client = await ClientAsync();

        var response = await client.PostAsync($"/api/agent-runs/{runId}/cancel", null);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var (status, error) = await ReadRunAsync(runId);
        Assert.Equal(AgentRunStatuses.Failed, status);
        Assert.Equal(CancelAgentRunCommandHandler.CancelledByUserMessage, error);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HermesDbContext>();
        var approval = db.TaskApprovals.Single(a => a.ChatSessionId == sessionId);
        Assert.Equal(TaskApproval.CancelledStatus, approval.Status);
        Assert.NotNull(approval.ResolvedAtUtc);
    }

    [Fact]
    public async Task Cancel_RunParkedForResume_Closes()
    {
        var (runId, _) = await SeedRunAsync(AgentRunStatuses.Running, AgentRunResumeStates.Pending);
        var client = await ClientAsync();

        Assert.Equal(HttpStatusCode.NoContent,
            (await client.PostAsync($"/api/agent-runs/{runId}/cancel", null)).StatusCode);
        Assert.Equal(AgentRunStatuses.Failed, (await ReadRunAsync(runId)).Status);
    }

    [Fact]
    public async Task Cancel_OrphanedRunningRun_Closes_ButFreshStreamingRunIs409()
    {
        var client = await ClientAsync();

        var (freshId, _) = await SeedRunAsync(AgentRunStatuses.Running);
        var fresh = await client.PostAsync($"/api/agent-runs/{freshId}/cancel", null);
        Assert.Equal(HttpStatusCode.Conflict, fresh.StatusCode);
        Assert.Contains("stream", await fresh.Content.ReadAsStringAsync());
        Assert.Equal(AgentRunStatuses.Running, (await ReadRunAsync(freshId)).Status);

        var (orphanId, _) = await SeedRunAsync(
            AgentRunStatuses.Running, createdAtUtc: DateTime.UtcNow.AddMinutes(-30));
        Assert.Equal(HttpStatusCode.NoContent,
            (await client.PostAsync($"/api/agent-runs/{orphanId}/cancel", null)).StatusCode);
    }

    [Fact]
    public async Task Cancel_FinishedRun_Is409_AndForeignRunIs404()
    {
        var client = await ClientAsync();

        var (doneId, _) = await SeedRunAsync(
            AgentRunStatuses.Completed, completedAtUtc: DateTime.UtcNow.AddMinutes(-1));
        Assert.Equal(HttpStatusCode.Conflict,
            (await client.PostAsync($"/api/agent-runs/{doneId}/cancel", null)).StatusCode);

        Assert.Equal(HttpStatusCode.NotFound,
            (await client.PostAsync($"/api/agent-runs/{Guid.NewGuid()}/cancel", null)).StatusCode);
    }

    [Fact]
    public async Task List_ReturnsThePagedShape_AndSearchFiltersByGoal()
    {
        var marker = $"goal-{Guid.NewGuid():N}";
        Guid runId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HermesDbContext>();
            var session = new ChatSession
            {
                TenantId = LmKitApiFactory.TenantId,
                UserId = LmKitApiFactory.UserId,
                Title = "run",
                IsAgentRun = true
            };
            var run = new AgentRun
            {
                TenantId = LmKitApiFactory.TenantId,
                UserId = LmKitApiFactory.UserId,
                ChatSessionId = session.Id,
                Goal = marker,
                Status = AgentRunStatuses.Completed,
                CompletedAtUtc = DateTime.UtcNow
            };
            db.Add(session);
            db.Add(run);
            await db.SaveChangesAsync();
            runId = run.Id;
        }

        var client = await ClientAsync();
        var page = await client.GetFromJsonAsync<JsonElement>($"/api/agent-runs?search={marker}&page=1&pageSize=5");
        Assert.Equal(1, page.GetProperty("totalCount").GetInt32());
        var row = Assert.Single(page.GetProperty("items").EnumerateArray().ToArray());
        Assert.Equal(runId, row.GetProperty("id").GetGuid());
    }

    private async Task<HttpClient> ClientAsync()
    {
        await ClientGate.WaitAsync();
        try
        {
            if (_client is not null) return _client;
            var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
                HandleCookies = true
            });
            var login = await client.PostAsJsonAsync("/api/auth/login",
                new { email = LmKitApiFactory.Email, password = LmKitApiFactory.Password });
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
            return _client = client;
        }
        finally { ClientGate.Release(); }
    }
}
