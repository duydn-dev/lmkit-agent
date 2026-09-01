using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Client;

namespace LmKitOmniApi.Infrastructure.AI.Mcp;

public interface IMcpProtocolClient
{
    Task<IReadOnlyList<McpProtocolTool>> ListToolsAsync(
        Uri endpoint,
        string serverName,
        IReadOnlyDictionary<string, string> headers,
        CancellationToken ct);

    Task<McpProtocolCallResult> CallToolAsync(
        Uri endpoint,
        string serverName,
        IReadOnlyDictionary<string, string> headers,
        string toolName,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken ct);
}

public sealed record McpProtocolTool(string Name, string Description, JsonElement InputSchema, bool IsReadOnly);
public sealed record McpProtocolCallResult(bool IsError, string Content);

/// <summary>
/// Standards-compliant MCP client backed by the official C# SDK. The SDK negotiates
/// the current stateless 2026-07-28 protocol and falls back to initialize/session based
/// protocol revisions for older Streamable HTTP servers.
/// </summary>
public sealed class McpProtocolClient : IMcpProtocolClient
{
    private static readonly TimeSpan ConnectionTimeout = TimeSpan.FromSeconds(10);
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILoggerFactory _loggerFactory;

    public McpProtocolClient(IHttpClientFactory httpClientFactory, ILoggerFactory loggerFactory)
    {
        _httpClientFactory = httpClientFactory;
        _loggerFactory = loggerFactory;
    }

    public async Task<IReadOnlyList<McpProtocolTool>> ListToolsAsync(
        Uri endpoint,
        string serverName,
        IReadOnlyDictionary<string, string> headers,
        CancellationToken ct)
    {
        await using var client = await CreateClientAsync(endpoint, serverName, headers, ct);
        var tools = await client.ListToolsAsync(cancellationToken: ct);
        return tools.Select(tool => MapTool(tool.ProtocolTool)).ToArray();
    }

    public async Task<McpProtocolCallResult> CallToolAsync(
        Uri endpoint,
        string serverName,
        IReadOnlyDictionary<string, string> headers,
        string toolName,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken ct)
    {
        await using var client = await CreateClientAsync(endpoint, serverName, headers, ct);
        // Populate the SDK's known-tool cache before invocation so 2026-07-28
        // x-mcp-header parameters are validated and mirrored into Mcp-Param-* headers.
        var tools = await client.ListToolsAsync(cancellationToken: ct);
        var tool = tools.SingleOrDefault(candidate => candidate.Name.Equals(toolName, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"MCP tool '{toolName}' is no longer advertised by server '{serverName}'.");
        var result = await tool.CallAsync(arguments, cancellationToken: ct);
        return MapCallResult(result);
    }

    private async Task<McpClient> CreateClientAsync(
        Uri endpoint,
        string serverName,
        IReadOnlyDictionary<string, string> headers,
        CancellationToken ct)
    {
        var httpClient = _httpClientFactory.CreateClient("MCP");
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = endpoint,
                Name = $"lmkit-{serverName}",
                TransportMode = HttpTransportMode.StreamableHttp,
                ConnectionTimeout = ConnectionTimeout,
                EnableStandaloneGetStream = false,
                AdditionalHeaders = headers.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase)
            },
            httpClient,
            _loggerFactory,
            ownsHttpClient: false);

        return await McpClient.CreateAsync(transport, loggerFactory: _loggerFactory, cancellationToken: ct);
    }

    private static McpProtocolTool MapTool(ModelContextProtocol.Protocol.Tool tool)
    {
        var json = JsonSerializer.SerializeToElement(tool, McpJsonUtilities.DefaultOptions);
        var inputSchema = json.TryGetProperty("inputSchema", out var schema)
            ? schema.Clone()
            : JsonSerializer.SerializeToElement(new { type = "object", properties = new { } });
        var isReadOnly = json.TryGetProperty("annotations", out var annotations)
            && annotations.ValueKind == JsonValueKind.Object
            && annotations.TryGetProperty("readOnlyHint", out var readOnlyHint)
            && readOnlyHint.ValueKind == JsonValueKind.True;
        return new McpProtocolTool(tool.Name, tool.Description ?? string.Empty, inputSchema, isReadOnly);
    }

    private static McpProtocolCallResult MapCallResult(ModelContextProtocol.Protocol.CallToolResult result)
    {
        var json = JsonSerializer.SerializeToElement(result, McpJsonUtilities.DefaultOptions);
        var isError = json.TryGetProperty("isError", out var errorElement) && errorElement.ValueKind == JsonValueKind.True;
        var textParts = new List<string>();
        if (json.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
        {
            foreach (var block in content.EnumerateArray())
            {
                if (block.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                    textParts.Add(text.GetString() ?? string.Empty);
                else
                    textParts.Add(block.GetRawText());
            }
        }

        if (json.TryGetProperty("structuredContent", out var structured) && structured.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
            textParts.Add(structured.GetRawText());

        return new McpProtocolCallResult(isError, string.Join(Environment.NewLine, textParts));
    }
}
