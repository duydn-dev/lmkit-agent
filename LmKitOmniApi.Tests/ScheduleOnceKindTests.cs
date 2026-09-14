using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LmKitOmniApi.Application.Schedules;
using LmKitOmniApi.Application.Schedules.Commands;
using LmKitOmniApi.Domain.Entities;
using LmKitOmniApi.Infrastructure.AI;
using Microsoft.AspNetCore.Mvc.Testing;

namespace LmKitOmniApi.Tests;

/// <summary>
/// Loại lịch "once" (chạy 1 lần theo hẹn giờ): validate thời điểm tương lai,
/// NextRunUtc = đúng runAtUtc, và vòng đời sau-run (AdvanceAfterRun): bắn xong
/// tự TẮT, riêng Skipped (bận tạm thời) thử lại sau ~10 phút và vẫn bật.
/// </summary>
[Collection("DbSqlite")]
public sealed class ScheduleOnceKindTests : IClassFixture<LmKitApiFactory>
{
    private static readonly SemaphoreSlim ClientGate = new(1, 1);
    private static HttpClient? _client;

    private readonly LmKitApiFactory _factory;

    public ScheduleOnceKindTests(LmKitApiFactory factory)
    {
        _factory = factory;
        _factory.EnsureSeeded();
    }

    // ── AdvanceAfterRun (thuần logic) ────────────────────────────────────

    private static ScheduledTask OnceTask(DateTime runAtUtc) => new()
    {
        Name = "một lần",
        Prompt = "x",
        ScheduleKind = ScheduleCalculator.OnceKind,
        Enabled = true,
        NextRunUtc = runAtUtc
    };

    [Theory]
    [InlineData("Succeeded")]
    [InlineData("Failed")]
    [InlineData("AwaitingApproval")]
    public void Once_AfterItsSingleShot_DisablesItself(string status)
    {
        var now = DateTime.UtcNow;
        var task = OnceTask(now);

        ScheduledTaskRules.AdvanceAfterRun(task, status, now);

        Assert.False(task.Enabled); // đã bắn phát duy nhất — không bao giờ chạy lại
    }

    [Fact]
    public void Once_WhenSkipped_RetriesInTenMinutes_AndStaysEnabled()
    {
        var now = DateTime.UtcNow;
        var task = OnceTask(now);

        ScheduledTaskRules.AdvanceAfterRun(task, "Skipped", now);

        Assert.True(task.Enabled);
        Assert.Equal(now.AddMinutes(10), task.NextRunUtc);
    }

    [Fact]
    public void RecurringKinds_KeepTheirExistingAdvanceSemantics()
    {
        var now = DateTime.UtcNow;
        var task = new ScheduledTask
        {
            Name = "chu kỳ", Prompt = "x",
            ScheduleKind = ScheduleCalculator.IntervalKind,
            IntervalMinutes = 60, Enabled = true
        };

        ScheduledTaskRules.AdvanceAfterRun(task, "Succeeded", now);
        Assert.True(task.Enabled);
        Assert.Equal(now.AddMinutes(60), task.NextRunUtc);

        // Skipped: thử lại sau 10 phút vì sớm hơn nhịp 60 phút.
        ScheduledTaskRules.AdvanceAfterRun(task, "Skipped", now);
        Assert.Equal(now.AddMinutes(10), task.NextRunUtc);
    }

    // ── API contract ─────────────────────────────────────────────────────

    [Fact]
    public async Task Create_Once_PersistsTheExactInstant_AndPastInstantsAre400()
    {
        var client = await ClientAsync();
        var runAt = DateTime.UtcNow.AddHours(2);

        var created = await client.PostAsJsonAsync("/api/schedules", new
        {
            name = "Nhắc nộp báo cáo",
            prompt = "Nhắc tôi nộp báo cáo quan trắc.",
            scheduleKind = "once",
            runAtUtc = runAt
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var dto = await created.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("once", dto.GetProperty("scheduleKind").GetString());
        Assert.Equal(runAt, dto.GetProperty("nextRunUtc").GetDateTime(), TimeSpan.FromSeconds(1));
        Assert.True(dto.GetProperty("enabled").GetBoolean());

        var past = await client.PostAsJsonAsync("/api/schedules", new
        {
            name = "Quá khứ",
            prompt = "x",
            scheduleKind = "once",
            runAtUtc = DateTime.UtcNow.AddMinutes(-5)
        });
        Assert.Equal(HttpStatusCode.BadRequest, past.StatusCode);
        Assert.Contains("tương lai", await past.Content.ReadAsStringAsync());

        var missing = await client.PostAsJsonAsync("/api/schedules", new
        {
            name = "Thiếu giờ",
            prompt = "x",
            scheduleKind = "once"
        });
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Contains("runAtUtc", await missing.Content.ReadAsStringAsync());
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
