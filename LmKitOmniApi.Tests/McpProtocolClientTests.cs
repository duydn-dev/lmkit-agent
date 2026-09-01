using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using LmKitOmniApi.Infrastructure.AI.Mcp;
using Microsoft.Extensions.Logging;

namespace LmKitOmniApi.Tests;

public sealed class McpProtocolClientTests
{
    [Fact]
    public async Task UsesModernMcpJsonRpcForDiscoveryAndInvocation()
    {
        var handler = new ModernMcpHandler();
        using var httpClient = new HttpClient(handler);
        using var loggerFactory = LoggerFactory.Create(_ => { });
        var protocol = new McpProtocolClient(new StubHttpClientFactory(httpClient), loggerFactory);
        var endpoint = new Uri("https://mcp.example.test/mcp");
        var headers = new Dictionary<string, string> { ["Authorization"] = "Bearer test-token" };

        var tools = await protocol.ListToolsAsync(endpoint, "test", headers, CancellationToken.None);
        var result = await protocol.CallToolAsync(
            endpoint,
            "test",
            headers,
            "lookup",
            new Dictionary<string, object?> { ["query"] = "LM-Kit" },
            CancellationToken.None);

        var tool = Assert.Single(tools);
        Assert.Equal("lookup", tool.Name);
        Assert.True(tool.IsReadOnly);
        Assert.Equal("string", tool.InputSchema.GetProperty("properties").GetProperty("query").GetProperty("type").GetString());
        Assert.False(result.IsError);
        Assert.Equal("found: LM-Kit", result.Content);
        Assert.Contains(handler.Requests, request => request.Method == "tools/list" && request.McpMethod == "tools/list");
        Assert.Contains(handler.Requests, request => request.Method == "tools/call" && request.McpMethod == "tools/call"
            && request.McpName == "lookup" && request.McpParamQuery == "LM-Kit");
        Assert.All(handler.Requests, request => Assert.Equal("Bearer test-token", request.Authorization));
    }

    private sealed class StubHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class ModernMcpHandler : HttpMessageHandler
    {
        public ConcurrentBag<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            using var json = JsonDocument.Parse(body);
            var id = json.RootElement.TryGetProperty("id", out var idElement) ? idElement.GetRawText() : "null";
            var method = json.RootElement.GetProperty("method").GetString()!;
            Requests.Add(new RecordedRequest(
                method,
                request.Headers.TryGetValues("Mcp-Method", out var methods) ? methods.Single() : null,
                request.Headers.TryGetValues("Mcp-Name", out var names) ? names.Single() : null,
                request.Headers.TryGetValues("Mcp-Param-Query", out var queries) ? queries.Single() : null,
                request.Headers.Authorization?.ToString()));

            var result = method switch
            {
                "server/discover" => """{"supportedVersions":["2026-07-28"],"capabilities":{"tools":{}},"ttlMs":0,"cacheScope":"private"}""",
                "tools/list" => """{"tools":[{"name":"lookup","description":"Find a record","inputSchema":{"type":"object","properties":{"query":{"type":"string","x-mcp-header":"Query"}},"required":["query"],"additionalProperties":false},"annotations":{"readOnlyHint":true}}],"ttlMs":1000,"cacheScope":"private"}""",
                "tools/call" => """{"content":[{"type":"text","text":"found: LM-Kit"}],"isError":false}""",
                _ => throw new InvalidOperationException($"Unexpected MCP method {method}")
            };
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($$"""{"jsonrpc":"2.0","id":{{id}},"result":{{result}}}""", Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed record RecordedRequest(string Method, string? McpMethod, string? McpName, string? McpParamQuery, string? Authorization);
}
