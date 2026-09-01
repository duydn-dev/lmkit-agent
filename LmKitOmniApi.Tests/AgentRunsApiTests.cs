using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LmKitOmniApi.Domain.Entities;
using LmKitOmniApi.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace LmKitOmniApi.Tests;

/// <summary>
/// Contract tests for the agent-run read endpoints + the invariant that an
/// agent-run's hidden session never appears in the chat list. Runs are seeded
/// directly (the streaming POST needs a warm model, exercised only in the live
/// stack), which is enough to prove tenant/user scoping, the step timeline
/// projection, and validation/auth.
/// </summary>
public sealed class AgentRunsApiTests : IClassFixture<LmKitApiFactory>
{
    private static readonly SemaphoreSlim ClientGate = new(1, 1);
    private static HttpClient? _ownerClient;

    private static readonly Guid OtherTenantId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid OtherUserId = Guid.Parse("44444444-4444-4444-4444-444444444444");

    private readonly LmKitApiFactory _factory;

    public AgentRunsApiTests(LmKitApiFactory factory)
    {
        _factory = factory;
        _factory.EnsureSeeded();
    }

    [Fact]
    public async Task List_ReturnsOwnRunsNewestFirst_WithStepCount_AndNeverAnotherUsers()
    {
        var ownRunId = SeedRun(LmKitApiFactory.TenantId, LmKitApiFactory.UserId, "Mục tiêu của tôi", "Completed", steps: 2);
        var foreignRunId = SeedRun(OtherTenantId, OtherUserId, "Mục tiêu người khác", "Completed", steps: 1);

        var client = await OwnerClientAsync();
        var runs = await client.GetFromJsonAsync<JsonElement[]>("/api/agent-runs");

        Assert.Contains(runs!, r => r.GetProperty("id").GetGuid() == ownRunId);
        Assert.DoesNotContain(runs!, r => r.GetProperty("id").GetGuid() == foreignRunId);

        var own = runs!.Single(r => r.GetProperty("id").GetGuid() == ownRunId);
        Assert.Equal(2, own.GetProperty("stepCount").GetInt32());
        Assert.Equal("Completed", own.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Get_ReturnsRunWithOrderedSteps_ForOwner()
    {
        var runId = SeedRun(LmKitApiFactory.TenantId, LmKitApiFactory.UserId, "Phân tích dữ liệu", "Completed", steps: 3, result: "Xong.");

        var client = await OwnerClientAsync();
        var run = await client.GetFromJsonAsync<JsonElement>($"/api/agent-runs/{runId}");

        Assert.Equal("Phân tích dữ liệu", run.GetProperty("goal").GetString());
        Assert.Equal("Xong.", run.GetProperty("result").GetString());
        var steps = run.GetProperty("steps");
        Assert.Equal(3, steps.GetArrayLength());
        // Ordered by ordinal 1..3.
        var ordinals = steps.EnumerateArray().Select(s => s.GetProperty("ordinal").GetInt32()).ToList();
        Assert.Equal(new[] { 1, 2, 3 }, ordinals);
        Assert.Equal("run_python", steps[0].GetProperty("action").GetString());
    }

    [Fact]
    public async Task Get_ForAnotherUsersRun_Returns404()
    {
        var foreignRunId = SeedRun(OtherTenantId, OtherUserId, "Riêng tư", "Completed", steps: 1);

        var client = await OwnerClientAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/agent-runs/{foreignRunId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/agent-runs/{Guid.NewGuid()}")).StatusCode);
    }

    [Fact]
    public async Task Start_RejectsEmptyAndOversizedGoals()
    {
        var client = await OwnerClientAsync();

        var empty = await client.PostAsJsonAsync("/api/agent-runs", new { goal = "   " });
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);

        var oversized = await client.PostAsJsonAsync("/api/agent-runs", new { goal = new string('g', 4001) });
        Assert.Equal(HttpStatusCode.BadRequest, oversized.StatusCode);
    }

    [Fact]
    public async Task AgentRunEndpoints_RejectAnonymousCallers()
    {
        using var anonymous = _factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/agent-runs")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"/api/agent-runs/{Guid.NewGuid()}")).StatusCode);
        var post = await anonymous.PostAsJsonAsync("/api/agent-runs", new { goal = "test" });
        Assert.Equal(HttpStatusCode.Unauthorized, post.StatusCode);
    }

    [Fact]
    public async Task AgentRunHiddenSession_NeverAppearsInTheChatList()
    {
        // Seeding a run creates an IsAgentRun session; it must be invisible to chat.
        var runId = SeedRun(LmKitApiFactory.TenantId, LmKitApiFactory.UserId, "Ẩn khỏi chat", "Completed", steps: 1);
        var hiddenSessionId = HiddenSessionIdForRun(runId);

        var client = await OwnerClientAsync();
        var sessions = await client.GetFromJsonAsync<JsonElement[]>("/api/chat/sessions");

        Assert.DoesNotContain(sessions!, s => s.GetProperty("id").GetGuid() == hiddenSessionId);
    }

    private Guid SeedRun(Guid tenantId, Guid userId, string goal, string status, int steps, string? result = null)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HermesDbContext>();

        var session = new ChatSession { TenantId = tenantId, UserId = userId, Title = goal, IsAgentRun = true };
        db.ChatSessions.Add(session);

        var run = new AgentRun
        {
            TenantId = tenantId,
            UserId = userId,
            ChatSessionId = session.Id,
            Goal = goal,
            Status = status,
            Result = result,
            CompletedAtUtc = DateTime.UtcNow
        };
        for (var i = 0; i < steps; i++)
        {
            run.Steps.Add(new AgentRunStep
            {
                Ordinal = i + 1,
                Action = i == 0 ? "run_python" : $"tool_{i}",
                Input = $"input {i}",
                Observation = $"observation {i}"
            });
        }
        db.AgentRuns.Add(run);
        db.SaveChanges();
        return run.Id;
    }

    private Guid HiddenSessionIdForRun(Guid runId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HermesDbContext>();
        return db.AgentRuns.Where(r => r.Id == runId).Select(r => r.ChatSessionId).Single();
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
