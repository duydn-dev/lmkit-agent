using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;
using LmKitOmniApi.Infrastructure.Data;
using LmKitOmniApi.Domain.Entities;

namespace LmKitOmniApi.Infrastructure.AI.Mcp;

/// <summary>
/// MCP (Model Context Protocol) Client Service.
/// Connects to external MCP servers for dynamic tool discovery and invocation.
/// </summary>
public class McpClientService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<McpClientService> _logger;

    // Cache discovered tools from MCP servers per Tenant
    private readonly Dictionary<Guid, Dictionary<string, List<McpToolDefinition>>> _cachedTools = new();
    private readonly Dictionary<Guid, DateTime> _lastDiscovery = new();
    private readonly SemaphoreSlim _cacheLock = new(1, 1);
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(10);

    public McpClientService(
        IHttpClientFactory httpClientFactory,
        IServiceScopeFactory scopeFactory,
        ILogger<McpClientService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <summary>
    /// Discover available tools from all configured MCP servers for a specific tenant.
    /// Caches results for 10 minutes.
    /// </summary>
    public async Task<List<McpToolDefinition>> DiscoverToolsAsync(Guid tenantId, CancellationToken ct = default)
    {
        await _cacheLock.WaitAsync(ct);
        try
        {
            if (_lastDiscovery.TryGetValue(tenantId, out var lastTime) && 
                DateTime.UtcNow - lastTime < CacheDuration && 
                _cachedTools.TryGetValue(tenantId, out var tenantCache) && 
                tenantCache.Count > 0)
            {
                return tenantCache.Values.SelectMany(t => t).ToList();
            }

            if (!_cachedTools.ContainsKey(tenantId))
            {
                _cachedTools[tenantId] = new Dictionary<string, List<McpToolDefinition>>();
            }
            
            _cachedTools[tenantId].Clear();

            using var scope = _scopeFactory.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<HermesDbContext>();

            var mcpEndpoints = await dbContext.ExternalMcpServers
                .Where(x => x.TenantId == tenantId && x.IsActive)
                .ToListAsync(ct);

            foreach (var server in mcpEndpoints)
            {
                try
                {
                    var tools = await DiscoverToolsFromServerAsync(server, ct);
                    _cachedTools[tenantId][server.Name] = tools;
                    _logger.LogInformation("🔗 [MCP] Discovered {Count} tools from '{Server}' for Tenant {Tenant}", tools.Count, server.Name, tenantId);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("⚠️ [MCP] Failed to discover tools from '{Server}' for Tenant {Tenant}: {Error}", server.Name, tenantId, ex.Message);
                }
            }

            _lastDiscovery[tenantId] = DateTime.UtcNow;
            return _cachedTools[tenantId].Values.SelectMany(t => t).ToList();
        }
        finally
        {
            _cacheLock.Release();
        }
    }

    /// <summary>
    /// Invoke a tool on an MCP server by name.
    /// </summary>
    public async Task<McpInvocationResult> InvokeToolAsync(Guid tenantId, string toolName, Dictionary<string, object> parameters, CancellationToken ct = default)
    {
        // Find which server hosts this tool
        string? serverName = null;
        
        if (_cachedTools.TryGetValue(tenantId, out var tenantCache))
        {
            serverName = tenantCache
                .FirstOrDefault(kv => kv.Value.Any(t => t.Name.Equals(toolName, StringComparison.OrdinalIgnoreCase)))
                .Key;
        }

        if (string.IsNullOrEmpty(serverName))
        {
            // Try discovery first
            await DiscoverToolsAsync(tenantId, ct);
            if (_cachedTools.TryGetValue(tenantId, out var retryCache))
            {
                serverName = retryCache
                    .FirstOrDefault(kv => kv.Value.Any(t => t.Name.Equals(toolName, StringComparison.OrdinalIgnoreCase)))
                    .Key;
            }
        }

        if (string.IsNullOrEmpty(serverName))
        {
            return McpInvocationResult.Fail($"Tool '{toolName}' not found on any MCP server for Tenant '{tenantId}'.");
        }

        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<HermesDbContext>();
        
        var server = await dbContext.ExternalMcpServers
            .FirstOrDefaultAsync(s => s.TenantId == tenantId && s.Name == serverName && s.IsActive, ct);

        if (server == null)
        {
            return McpInvocationResult.Fail($"Server '{serverName}' configuration not found or inactive for Tenant '{tenantId}'.");
        }

        try
        {
            var client = _httpClientFactory.CreateClient("MCP");
            client.BaseAddress = new Uri(server.Url);
            client.Timeout = TimeSpan.FromSeconds(30);

            ApplyHeaders(client, server.HeadersJson);

            var request = new McpToolInvocationRequest
            {
                ToolName = toolName,
                Parameters = parameters
            };

            var response = await client.PostAsJsonAsync("/mcp/invoke", request, ct);
            
            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadFromJsonAsync<McpToolResponse>(cancellationToken: ct);
                _logger.LogInformation("🔗 [MCP] Tool '{Tool}' invoked successfully on '{Server}' (Tenant {Tenant})", toolName, serverName, tenantId);
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
    public async Task<string> GetToolDirectoryAsync(Guid tenantId, CancellationToken ct = default)
    {
        var tools = await DiscoverToolsAsync(tenantId, ct);
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

    private async Task<List<McpToolDefinition>> DiscoverToolsFromServerAsync(ExternalMcpServer server, CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient("MCP");
        client.BaseAddress = new Uri(server.Url);
        client.Timeout = TimeSpan.FromSeconds(10);

        ApplyHeaders(client, server.HeadersJson);

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
    
    private void ApplyHeaders(HttpClient client, string? headersJson)
    {
        if (string.IsNullOrWhiteSpace(headersJson)) return;
        
        try
        {
            var headers = JsonSerializer.Deserialize<Dictionary<string, string>>(headersJson);
            if (headers != null)
            {
                foreach (var header in headers)
                {
                    if (header.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase) && header.Value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                    {
                        var token = header.Value.Substring(7).Trim();
                        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
                    }
                    else
                    {
                        client.DefaultRequestHeaders.TryAddWithoutValidation(header.Key, header.Value);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "⚠️ [MCP] Failed to parse HeadersJson");
        }
    }
}

// ---- Models ----

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
