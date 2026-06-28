using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace LmKitOmniApi.Infrastructure.AI.Mcp;

/// <summary>
/// MCP (Model Context Protocol) Client Service.
/// Connects to external MCP servers for dynamic tool discovery and invocation.
/// Inspired by console_net/ai-agents/mcp-client.
/// 
/// MCP enables the AI agent to discover and use tools from external services
/// without hardcoding them into the codebase.
/// </summary>
public class McpClientService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<McpClientService> _logger;

    // Cache discovered tools from MCP servers
    private readonly Dictionary<string, List<McpToolDefinition>> _cachedTools = new();
    private readonly SemaphoreSlim _cacheLock = new(1, 1);
    private DateTime _lastDiscovery = DateTime.MinValue;
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(10);

    public McpClientService(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<McpClientService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>
    /// Discover available tools from all configured MCP servers.
    /// Caches results for 10 minutes.
    /// </summary>
    public async Task<List<McpToolDefinition>> DiscoverToolsAsync(CancellationToken ct = default)
    {
        await _cacheLock.WaitAsync(ct);
        try
        {
            if (DateTime.UtcNow - _lastDiscovery < CacheDuration && _cachedTools.Count > 0)
            {
                return _cachedTools.Values.SelectMany(t => t).ToList();
            }

            _cachedTools.Clear();
            var mcpEndpoints = _configuration.GetSection("Mcp:Servers").Get<List<McpServerConfig>>() ?? new();

            foreach (var server in mcpEndpoints)
            {
                try
                {
                    var tools = await DiscoverToolsFromServerAsync(server, ct);
                    _cachedTools[server.Name] = tools;
                    _logger.LogInformation("🔗 [MCP] Discovered {Count} tools from '{Server}'", tools.Count, server.Name);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("⚠️ [MCP] Failed to discover tools from '{Server}': {Error}", server.Name, ex.Message);
                }
            }

            _lastDiscovery = DateTime.UtcNow;
            return _cachedTools.Values.SelectMany(t => t).ToList();
        }
        finally
        {
            _cacheLock.Release();
        }
    }

    /// <summary>
    /// Invoke a tool on an MCP server by name.
    /// </summary>
    public async Task<McpInvocationResult> InvokeToolAsync(string toolName, Dictionary<string, object> parameters, CancellationToken ct = default)
    {
        // Find which server hosts this tool
        var serverName = _cachedTools
            .FirstOrDefault(kv => kv.Value.Any(t => t.Name.Equals(toolName, StringComparison.OrdinalIgnoreCase)))
            .Key;

        if (string.IsNullOrEmpty(serverName))
        {
            // Try discovery first
            await DiscoverToolsAsync(ct);
            serverName = _cachedTools
                .FirstOrDefault(kv => kv.Value.Any(t => t.Name.Equals(toolName, StringComparison.OrdinalIgnoreCase)))
                .Key;
        }

        if (string.IsNullOrEmpty(serverName))
        {
            return McpInvocationResult.Fail($"Tool '{toolName}' not found on any MCP server.");
        }

        var servers = _configuration.GetSection("Mcp:Servers").Get<List<McpServerConfig>>() ?? new();
        var server = servers.FirstOrDefault(s => s.Name == serverName);
        if (server == null)
        {
            return McpInvocationResult.Fail($"Server '{serverName}' configuration not found.");
        }

        try
        {
            var client = _httpClientFactory.CreateClient("MCP");
            client.BaseAddress = new Uri(server.BaseUrl);
            client.Timeout = TimeSpan.FromSeconds(30);

            if (!string.IsNullOrEmpty(server.ApiKey))
            {
                client.DefaultRequestHeaders.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", server.ApiKey);
            }

            var request = new McpToolInvocationRequest
            {
                ToolName = toolName,
                Parameters = parameters
            };

            var response = await client.PostAsJsonAsync("/mcp/invoke", request, ct);
            
            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadFromJsonAsync<McpToolResponse>(cancellationToken: ct);
                _logger.LogInformation("🔗 [MCP] Tool '{Tool}' invoked successfully on '{Server}'", toolName, serverName);
                return McpInvocationResult.Ok(result?.Content ?? "", toolName, serverName);
            }
            else
            {
                var errorBody = await response.Content.ReadAsStringAsync(ct);
                _logger.LogWarning("🔗 [MCP] Tool '{Tool}' invocation failed: {Status} {Body}", toolName, response.StatusCode, errorBody);
                return McpInvocationResult.Fail($"HTTP {response.StatusCode}: {errorBody}");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "🔗 [MCP] Error invoking tool '{Tool}'", toolName);
            return McpInvocationResult.Fail(ex.Message);
        }
    }

    /// <summary>
    /// Get a formatted list of MCP tools for injection into agent system prompt.
    /// </summary>
    public async Task<string> GetToolDirectoryAsync(CancellationToken ct = default)
    {
        var tools = await DiscoverToolsAsync(ct);
        if (tools.Count == 0) return string.Empty;

        var builder = new System.Text.StringBuilder();
        builder.AppendLine("\n--- Available MCP Tools ---");
        foreach (var tool in tools)
        {
            builder.AppendLine($"- {tool.Name}: {tool.Description} (Server: {tool.ServerName})");
            if (tool.Parameters.Count > 0)
            {
                builder.AppendLine($"  Parameters: {string.Join(", ", tool.Parameters.Select(p => $"{p.Name}({p.Type})"))}");
            }
        }
        builder.AppendLine("--- End MCP Tools ---");
        return builder.ToString();
    }

    private async Task<List<McpToolDefinition>> DiscoverToolsFromServerAsync(McpServerConfig server, CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient("MCP");
        client.BaseAddress = new Uri(server.BaseUrl);
        client.Timeout = TimeSpan.FromSeconds(10);

        if (!string.IsNullOrEmpty(server.ApiKey))
        {
            client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", server.ApiKey);
        }

        var response = await client.GetAsync("/mcp/tools", ct);
        response.EnsureSuccessStatusCode();

        var tools = await response.Content.ReadFromJsonAsync<List<McpToolDefinition>>(cancellationToken: ct) ?? new();
        
        // Tag each tool with its server name
        foreach (var tool in tools)
        {
            tool.ServerName = server.Name;
        }

        return tools;
    }
}

// ---- Models ----

public class McpServerConfig
{
    public string Name { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = string.Empty;
    public string? ApiKey { get; set; }
    public string? Description { get; set; }
}

public class McpToolDefinition
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string ServerName { get; set; } = string.Empty;
    public List<McpToolParameter> Parameters { get; set; } = new();
}

public class McpToolParameter
{
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = "string";
    public bool Required { get; set; }
    public string? Description { get; set; }
}

public class McpToolInvocationRequest
{
    public string ToolName { get; set; } = string.Empty;
    public Dictionary<string, object> Parameters { get; set; } = new();
}

public class McpToolResponse
{
    public string Content { get; set; } = string.Empty;
    public bool Success { get; set; }
}

public class McpInvocationResult
{
    public bool Success { get; set; }
    public string Content { get; set; } = string.Empty;
    public string? ToolName { get; set; }
    public string? ServerName { get; set; }
    public string? ErrorMessage { get; set; }

    public static McpInvocationResult Ok(string content, string tool, string server)
        => new() { Success = true, Content = content, ToolName = tool, ServerName = server };
    public static McpInvocationResult Fail(string error)
        => new() { Success = false, ErrorMessage = error };
}
