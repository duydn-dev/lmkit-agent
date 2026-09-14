using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace LmKitOmniApi.Tests;

/// <summary>
/// Trường mới của lịch qua API: persona (customAgentId) phải là agent người tạo
/// dùng được; webhook chỉ lưu được khi vận hành bật ScheduleWebhooks và URL qua
/// SSRF/allowlist. Host mặc định (webhook TẮT) và host bật riêng đều được thử.
/// </summary>
[Collection("DbSqlite")]
public sealed class ScheduledTaskPersonaWebhookApiTests : IClassFixture<LmKitApiFactory>
{
    private static readonly SemaphoreSlim ClientGate = new(1, 1);
    private static HttpClient? _client;

    private readonly LmKitApiFactory _factory;

    public ScheduledTaskPersonaWebhookApiTests(LmKitApiFactory factory)
    {
        _factory = factory;
        _factory.EnsureSeeded();
    }

    [Fact]
    public async Task Create_WithOwnAgentPersona_PersistsIt_AndUnknownAgentIs400()
    {
        var client = await ClientAsync();

        var agentResponse = await client.PostAsJsonAsync("/api/agents/custom", new
        {
            name = $"Persona {Guid.NewGuid():N}"[..20],
            personaPrompt = "Bạn là chuyên viên tổng hợp báo cáo."
        });
        Assert.Equal(HttpStatusCode.Created, agentResponse.StatusCode);
        var agentId = (await agentResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var created = await client.PostAsJsonAsync("/api/schedules", new
        {
            name = "Lịch có persona",
            prompt = "Tổng hợp số liệu hôm nay.",
            runMode = "agent",
            customAgentId = agentId,
            scheduleKind = "daily",
            timeOfDayMinutes = 30
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var dto = await created.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(agentId, dto.GetProperty("customAgentId").GetGuid());

        var unknown = await client.PostAsJsonAsync("/api/schedules", new
        {
            name = "Persona lạ",
            prompt = "x",
            customAgentId = Guid.NewGuid(),
            scheduleKind = "daily",
            timeOfDayMinutes = 30
        });
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        Assert.Contains("persona", await unknown.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Webhook_WhenTheFeatureIsOff_IsRefusedWithAClearMessage()
    {
        var client = await ClientAsync();
        var response = await client.PostAsJsonAsync("/api/schedules", new
        {
            name = "Lịch webhook",
            prompt = "x",
            deliveryWebhookUrl = "https://hooks.example.com/bao-cao",
            scheduleKind = "daily",
            timeOfDayMinutes = 30
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("ScheduleWebhooks", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Webhook_WhenEnabled_AcceptsPublicUrls_AndRefusesInternalOrOffAllowlistOnes()
    {
        // Host riêng: bật webhook + allowlist một host. Overrides phải đặt TRƯỚC lần
        // đầu chạm Services/CreateClient (host build lazy).
        using var factory = new LmKitApiFactory();
        factory.ConfigurationOverrides["ScheduleWebhooks:Enabled"] = "true";
        factory.ConfigurationOverrides["ScheduleWebhooks:AllowedHosts:0"] = "8.8.8.8";
        factory.EnsureSeeded();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true
        });
        var login = await client.PostAsJsonAsync("/api/auth/login",
            new { email = LmKitApiFactory.Email, password = LmKitApiFactory.Password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        async Task<HttpResponseMessage> CreateWithWebhookAsync(string url) =>
            await client.PostAsJsonAsync("/api/schedules", new
            {
                name = $"Webhook {Guid.NewGuid():N}"[..20],
                prompt = "x",
                deliveryWebhookUrl = url,
                scheduleKind = "interval",
                intervalMinutes = 30
            });

        // Literal IP công khai trong allowlist → nhận (không cần DNS thật).
        var ok = await CreateWithWebhookAsync("http://8.8.8.8/hook");
        Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
        var dto = await ok.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("http://8.8.8.8/hook", dto.GetProperty("deliveryWebhookUrl").GetString());

        // Nội bộ → SSRF chặn dù feature bật.
        Assert.Equal(HttpStatusCode.BadRequest, (await CreateWithWebhookAsync("http://127.0.0.1/hook")).StatusCode);
        // Ngoài allowlist → chặn.
        var offList = await CreateWithWebhookAsync("http://1.1.1.1/hook");
        Assert.Equal(HttpStatusCode.BadRequest, offList.StatusCode);
        Assert.Contains("AllowedHosts", await offList.Content.ReadAsStringAsync());
        // Scheme lạ → chặn từ tầng shape-validate.
        Assert.Equal(HttpStatusCode.BadRequest, (await CreateWithWebhookAsync("ftp://8.8.8.8/hook")).StatusCode);
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
