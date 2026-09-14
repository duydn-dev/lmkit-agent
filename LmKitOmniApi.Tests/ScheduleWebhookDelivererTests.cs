using System.Net;
using System.Text.Json;
using LmKitOmniApi.Infrastructure.AI.Security;
using LmKitOmniApi.Infrastructure.AI.Web;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LmKitOmniApi.Tests;

/// <summary>
/// Kênh webhook của lịch: tắt mặc định, re-validate URL MỖI lần gửi (SSRF +
/// allowlist), payload đúng shape và result bị cắt trần, non-2xx/timeout thành
/// mô tả lỗi ngắn — không bao giờ ném đổ worker.
/// </summary>
public sealed class ScheduleWebhookDelivererTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest;
        public string? LastBody;
        public int CallCount;
        public HttpStatusCode Status = HttpStatusCode.OK;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            CallCount++;
            LastRequest = request;
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(Status);
        }
    }

    private sealed class StubFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;
        public StubFactory(HttpMessageHandler handler) => _handler = handler;
        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }

    private static (ScheduleWebhookDeliverer Deliverer, StubHandler Handler) Build(Action<ScheduleWebhookOptions>? configure = null)
    {
        var options = new ScheduleWebhookOptions { Enabled = true };
        configure?.Invoke(options);
        var handler = new StubHandler();
        var deliverer = new ScheduleWebhookDeliverer(
            new StubFactory(handler),
            new ToolSandboxService(NullLogger<ToolSandboxService>.Instance),
            Options.Create(options),
            NullLogger<ScheduleWebhookDeliverer>.Instance);
        return (deliverer, handler);
    }

    private static Task<string?> DeliverAsync(ScheduleWebhookDeliverer deliverer, string url, string result = "Báo cáo xong.")
        => deliverer.DeliverAsync(Guid.NewGuid(), "Lịch test", "agent", result, DateTime.UtcNow, url, CancellationToken.None);

    [Fact]
    public async Task Disabled_ReturnsError_WithoutSending()
    {
        var (deliverer, handler) = Build(o => o.Enabled = false);
        var error = await DeliverAsync(deliverer, "http://8.8.8.8/hook");
        Assert.Contains("đang tắt", error);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task InternalAddresses_AreRefused_BeforeSending()
    {
        var (deliverer, handler) = Build();
        Assert.NotNull(await DeliverAsync(deliverer, "http://127.0.0.1/hook"));
        Assert.NotNull(await DeliverAsync(deliverer, "http://10.1.2.3/hook"));
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task HostOutsideAllowlist_IsRefused()
    {
        var (deliverer, handler) = Build(o => o.AllowedHosts = ["hooks.example.com"]);
        var error = await DeliverAsync(deliverer, "https://evil.test/hook");
        Assert.Contains("AllowedHosts", error);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task Success_PostsTheJsonPayload_WithTruncatedResult()
    {
        var (deliverer, handler) = Build(o => o.MaxResultChars = 10);
        var error = await DeliverAsync(deliverer, "http://8.8.8.8/hook", new string('k', 100));

        Assert.Null(error);
        Assert.Equal(1, handler.CallCount);
        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        var payload = JsonSerializer.Deserialize<JsonElement>(handler.LastBody!);
        Assert.Equal("Lịch test", payload.GetProperty("taskName").GetString());
        Assert.Equal("agent", payload.GetProperty("runMode").GetString());
        Assert.Equal("Succeeded", payload.GetProperty("status").GetString());
        Assert.Equal(10, payload.GetProperty("result").GetString()!.Length);
    }

    [Fact]
    public async Task NonSuccessStatus_BecomesAShortError()
    {
        var (deliverer, handler) = Build();
        handler.Status = HttpStatusCode.InternalServerError;
        var error = await DeliverAsync(deliverer, "http://8.8.8.8/hook");
        Assert.Contains("HTTP 500", error);
    }
}
