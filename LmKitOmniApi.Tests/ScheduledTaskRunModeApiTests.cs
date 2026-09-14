using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace LmKitOmniApi.Tests;

/// <summary>
/// Contract tests for ScheduledTask.RunMode: default "completion" (client cũ không
/// gửi trường này), opt-in "agent", giá trị lạ bị 400, và getlist trả trường mới
/// theo shape phân trang chuẩn.
/// </summary>
[Collection("DbSqlite")]
public sealed class ScheduledTaskRunModeApiTests : IClassFixture<LmKitApiFactory>
{
    private static readonly SemaphoreSlim ClientGate = new(1, 1);
    private static HttpClient? _client;

    private readonly LmKitApiFactory _factory;

    public ScheduledTaskRunModeApiTests(LmKitApiFactory factory)
    {
        _factory = factory;
        _factory.EnsureSeeded();
    }

    [Fact]
    public async Task Create_WithoutRunMode_DefaultsToCompletion()
    {
        var client = await ClientAsync();
        var created = await client.PostAsJsonAsync("/api/schedules", new
        {
            name = $"Báo cáo thường {Guid.NewGuid():N}"[..30],
            prompt = "Tóm tắt tình hình hôm nay.",
            scheduleKind = "daily",
            timeOfDayMinutes = 60
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var dto = await created.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("completion", dto.GetProperty("runMode").GetString());
    }

    [Fact]
    public async Task Create_WithAgentRunMode_PersistsIt_AndListReturnsIt()
    {
        var client = await ClientAsync();
        var name = $"Query CSDL {Guid.NewGuid():N}"[..30];
        var created = await client.PostAsJsonAsync("/api/schedules", new
        {
            name,
            prompt = "Đếm số đơn hàng hôm qua trong CSDL kho-bao-cao và trình bày dạng bảng.",
            runMode = "AGENT", // hoa/thường không phân biệt
            scheduleKind = "interval",
            intervalMinutes = 30
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var dto = await created.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("agent", dto.GetProperty("runMode").GetString());

        var page = await client.GetFromJsonAsync<JsonElement>(
            $"/api/schedules?search={Uri.EscapeDataString(name)}");
        var row = Assert.Single(page.GetProperty("items").EnumerateArray().ToArray());
        Assert.Equal("agent", row.GetProperty("runMode").GetString());
    }

    [Fact]
    public async Task Create_WithUnknownRunMode_Is400()
    {
        var client = await ClientAsync();
        var created = await client.PostAsJsonAsync("/api/schedules", new
        {
            name = "Chế độ lạ",
            prompt = "x",
            runMode = "cron-job",
            scheduleKind = "daily",
            timeOfDayMinutes = 0
        });
        Assert.Equal(HttpStatusCode.BadRequest, created.StatusCode);
        Assert.Contains("Chế độ chạy", await created.Content.ReadAsStringAsync());
    }

    private async Task<HttpClient> ClientAsync()
    {
        await ClientGate.WaitAsync();
        try { return _client ??= await LoginAsync(); }
        finally { ClientGate.Release(); }
    }

    private async Task<HttpClient> LoginAsync()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true
        });
        var login = await client.PostAsJsonAsync("/api/auth/login",
            new { email = LmKitApiFactory.Email, password = LmKitApiFactory.Password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        return client;
    }
}
