using System.Net;
using LmKitOmniApi.Infrastructure.AI.Security;
using LmKitOmniApi.Infrastructure.AI.Web;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LmKitOmniApi.Tests;

/// <summary>
/// Hermetic tests for the generic REST tool: feature gating, method separation
/// (call_api chỉ GET/HEAD, call_api_write chỉ các phương thức ghi), header
/// allowlist, host allowlist, SSRF refusal, redirect-not-followed and response
/// truncation — all through a fake handler, no network.
/// </summary>
public sealed class ApiCallServiceTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest;
        public string? LastRequestBody;
        public int CallCount;
        public Func<HttpRequestMessage, HttpResponseMessage> Responder = _ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"ok\":true}") };

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            CallCount++;
            LastRequest = request;
            LastRequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return Responder(request);
        }
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;
        public StubHttpClientFactory(HttpMessageHandler handler) => _handler = handler;
        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }

    private static (ApiCallService Service, StubHandler Handler) Build(Action<ApiCallOptions>? configure = null)
    {
        var options = new ApiCallOptions { Enabled = true };
        configure?.Invoke(options);
        var handler = new StubHandler();
        var service = new ApiCallService(
            new StubHttpClientFactory(handler),
            new ToolSandboxService(NullLogger<ToolSandboxService>.Instance),
            Options.Create(options),
            NullLogger<ApiCallService>.Instance);
        return (service, handler);
    }

    [Fact]
    public async Task WhenDisabled_ReturnsTheOperatorMessage_WithoutAnyRequest()
    {
        var (service, handler) = Build(o => o.Enabled = false);
        var result = await service.ExecuteAsync("http://8.8.8.8/data", allowWrite: false, CancellationToken.None);
        Assert.Contains("chưa được bật", result);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task ReadTool_RefusesWriteMethods_AndPointsAtTheWriteTool()
    {
        var (service, handler) = Build();
        var result = await service.ExecuteAsync(
            "{\"method\":\"POST\",\"url\":\"http://8.8.8.8/data\"}", allowWrite: false, CancellationToken.None);
        Assert.Contains("call_api_write", result);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task WriteTool_RefusesGet()
    {
        var (service, handler) = Build();
        var result = await service.ExecuteAsync(
            "{\"method\":\"GET\",\"url\":\"http://8.8.8.8/data\"}", allowWrite: true, CancellationToken.None);
        Assert.Contains("call_api", result);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task HeadersOutsideTheAllowlist_AreRefused()
    {
        var (service, handler) = Build();
        var result = await service.ExecuteAsync(
            "{\"url\":\"http://8.8.8.8/data\",\"headers\":{\"Cookie\":\"session=1\"}}",
            allowWrite: false, CancellationToken.None);
        Assert.Contains("Cookie", result);
        Assert.Contains("không nằm trong danh sách", result);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task HostOutsideTheAllowlist_IsRefused()
    {
        var (service, handler) = Build(o => o.AllowedHosts = ["api.example.com"]);
        var result = await service.ExecuteAsync("https://evil.test/steal", allowWrite: false, CancellationToken.None);
        Assert.Contains("AllowedHosts", result);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task PrivateAddresses_AreRefusedBeforeAnyRequest()
    {
        var (service, handler) = Build();
        var loopback = await service.ExecuteAsync("http://127.0.0.1/admin", allowWrite: false, CancellationToken.None);
        var rfc1918 = await service.ExecuteAsync("http://10.0.0.5/internal", allowWrite: false, CancellationToken.None);
        Assert.Contains("nội bộ", loopback);
        Assert.Contains("nội bộ", rfc1918);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task BareUrl_IsAGet_AndAllowlistedHeadersTravel()
    {
        var (service, handler) = Build();
        var result = await service.ExecuteAsync(
            "{\"url\":\"http://8.8.8.8/data\",\"headers\":{\"Authorization\":\"Bearer token-1\",\"Accept\":\"application/json\"}}",
            allowWrite: false, CancellationToken.None);

        Assert.StartsWith("HTTP 200", result);
        Assert.Contains("{\"ok\":true}", result);
        Assert.Equal(HttpMethod.Get, handler.LastRequest!.Method);
        Assert.Equal("Bearer token-1", handler.LastRequest.Headers.Authorization!.ToString());
    }

    [Fact]
    public async Task WriteTool_SendsBodyAndContentType()
    {
        var (service, handler) = Build();
        var result = await service.ExecuteAsync(
            "{\"method\":\"POST\",\"url\":\"http://8.8.8.8/orders\",\"headers\":{\"Content-Type\":\"application/json\"},\"body\":{\"qty\":2}}",
            allowWrite: true, CancellationToken.None);

        Assert.StartsWith("HTTP 200", result);
        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.Equal("{\"qty\":2}", handler.LastRequestBody);
        Assert.Equal("application/json", handler.LastRequest.Content!.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task Redirects_AreReported_NeverFollowed()
    {
        var (service, handler) = Build();
        handler.Responder = _ =>
        {
            var redirect = new HttpResponseMessage(HttpStatusCode.Found);
            redirect.Headers.Location = new Uri("http://8.8.4.4/next");
            return redirect;
        };

        var result = await service.ExecuteAsync("http://8.8.8.8/start", allowWrite: false, CancellationToken.None);

        Assert.StartsWith("HTTP 302", result);
        Assert.Contains("http://8.8.4.4/next", result);
        Assert.Equal(1, handler.CallCount); // đúng MỘT request — không tự đi theo redirect
    }

    [Fact]
    public async Task LongBodies_AreTruncated()
    {
        var (service, handler) = Build(o => o.MaxResponseChars = 50);
        handler.Responder = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(new string('x', 5_000))
        };

        var result = await service.ExecuteAsync("http://8.8.8.8/big", allowWrite: false, CancellationToken.None);
        Assert.Contains("đã cắt bớt", result);
        Assert.True(result.Length < 1_000);
    }
}
