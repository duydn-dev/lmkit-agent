using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;
using LmKitOmniApi.Infrastructure.Data;
using LmKitOmniApi.Domain.Entities;
using LmKitOmniApi.Infrastructure.AI.Security;
using LmKitOmniApi.Infrastructure.Security;

namespace LmKitOmniApi.Infrastructure.AI.Mcp;

/// <summary>
/// MCP (Model Context Protocol) Client Service.
/// Connects to external MCP servers for dynamic tool discovery and invocation.
/// </summary>
public class McpClientService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<McpClientService> _logger;
    private readonly ToolSandboxService _sandbox;
    private readonly McpHeaderProtector _headerProtector;
    private readonly IMcpProtocolClient _protocolClient;
    private readonly IMcpOAuthTokenProvider _oauthTokenProvider;

    // Cache discovered tools from MCP servers per Tenant
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, IReadOnlyDictionary<string, List<McpToolDefinition>>> CachedTools = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, DateTime> LastDiscovery = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, SemaphoreSlim> TenantCacheLocks = new();
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(10);

    public McpClientService(
        IServiceScopeFactory scopeFactory,
        ToolSandboxService sandbox,
        McpHeaderProtector headerProtector,
        IMcpProtocolClient protocolClient,
        IMcpOAuthTokenProvider oauthTokenProvider,
        ILogger<McpClientService> logger)
    {
        _scopeFactory = scopeFactory;
        _sandbox = sandbox;
        _headerProtector = headerProtector;
        _protocolClient = protocolClient;
        _oauthTokenProvider = oauthTokenProvider;
        _logger = logger;
    }

    public async Task InvalidateTenantCacheAsync(Guid tenantId, CancellationToken ct = default)
    {
        var cacheLock = TenantCacheLocks.GetOrAdd(tenantId, static _ => new SemaphoreSlim(1, 1));
        await cacheLock.WaitAsync(ct);
        try
        {
            CachedTools.TryRemove(tenantId, out _);
            LastDiscovery.TryRemove(tenantId, out _);
        }
        finally
        {
            cacheLock.Release();
        }
    }

    /// <summary>
    /// Discover available tools from all configured MCP servers for a specific tenant.
    /// Caches results for 10 minutes.
    /// </summary>
    public async Task<List<McpToolDefinition>> DiscoverToolsAsync(Guid tenantId, CancellationToken ct = default)
    {
        var cacheLock = TenantCacheLocks.GetOrAdd(tenantId, static _ => new SemaphoreSlim(1, 1));
        await cacheLock.WaitAsync(ct);
        try
        {
            if (LastDiscovery.TryGetValue(tenantId, out var lastTime) &&
                DateTime.UtcNow - lastTime < CacheDuration && 
                CachedTools.TryGetValue(tenantId, out var tenantCache))
            {
                return tenantCache.Values.SelectMany(t => t).ToList();
            }

            var discoveredForTenant = new Dictionary<string, List<McpToolDefinition>>(StringComparer.OrdinalIgnoreCase);

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
                    discoveredForTenant[server.Name] = tools;
                    _logger.LogInformation("🔗 [MCP] Discovered {Count} tools from '{Server}' for Tenant {Tenant}", tools.Count, server.Name, tenantId);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("⚠️ [MCP] Failed to discover tools from '{Server}' for Tenant {Tenant}: {Error}", server.Name, tenantId, ex.Message);
                }
            }

            CachedTools[tenantId] = discoveredForTenant;
            LastDiscovery[tenantId] = DateTime.UtcNow;
            return discoveredForTenant.Values.SelectMany(t => t).ToList();
        }
        finally
        {
            cacheLock.Release();
        }
    }

    /// <summary>
    /// Invoke a tool on an MCP server by name.
    /// </summary>
    public async Task<McpInvocationResult> InvokeToolAsync(Guid tenantId, string serverName, string toolName, Dictionary<string, object> parameters, CancellationToken ct = default)
    {
        if (!CachedTools.TryGetValue(tenantId, out var tenantCache) ||
            !tenantCache.TryGetValue(serverName, out var serverTools) ||
            !serverTools.Any(tool => tool.Name.Equals(toolName, StringComparison.Ordinal)))
        {
            await DiscoverToolsAsync(tenantId, ct);
            if (!CachedTools.TryGetValue(tenantId, out tenantCache) ||
                !tenantCache.TryGetValue(serverName, out serverTools) ||
                !serverTools.Any(tool => tool.Name.Equals(toolName, StringComparison.Ordinal)))
                return McpInvocationResult.Fail($"Tool '{toolName}' was not discovered on MCP server '{serverName}'.");
        }

        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<HermesDbContext>();
        
        var server = await dbContext.ExternalMcpServers
            .FirstOrDefaultAsync(s => s.TenantId == tenantId && s.Name == serverName && s.IsActive, ct);

        if (server is null)
        {
            return McpInvocationResult.Fail($"Server '{serverName}' configuration not found or inactive for Tenant '{tenantId}'.");
        }

        try
        {
            var urlValidation = await _sandbox.ValidateUrlAsync(server.Url, ct);
            if (!urlValidation.IsAllowed)
                return McpInvocationResult.Fail(urlValidation.DenialReason ?? "MCP server URL was blocked.");

            var result = await _protocolClient.CallToolAsync(
                new Uri(server.Url),
                server.Name,
                await BuildRequestHeadersAsync(server, ct),
                toolName,
                parameters.ToDictionary(pair => pair.Key, pair => (object?)pair.Value),
                ct);

            if (result.IsError)
                return McpInvocationResult.Fail(string.IsNullOrWhiteSpace(result.Content) ? "MCP tool returned an error." : result.Content);

            _logger.LogInformation("🔗 [MCP] Tool '{Tool}' invoked successfully on '{Server}' (Tenant {Tenant})", toolName, serverName, tenantId);
            return McpInvocationResult.Ok(result.Content, toolName, serverName);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "🔗 [MCP] Error invoking tool '{Tool}'", toolName);
            return McpInvocationResult.Fail("MCP invocation failed.");
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
        var urlValidation = await _sandbox.ValidateUrlAsync(server.Url, ct);
        if (!urlValidation.IsAllowed)
            throw new InvalidOperationException(urlValidation.DenialReason ?? "MCP server URL was blocked.");

        var tools = await _protocolClient.ListToolsAsync(
            new Uri(server.Url), server.Name, await BuildRequestHeadersAsync(server, ct), ct);

        return tools.Select(tool => new McpToolDefinition
        {
            Name = tool.Name,
            Description = tool.Description,
            ServerName = server.Name,
            AllowAutomaticExecution = server.TrustReadOnlyAnnotations && tool.IsReadOnly,
            InputSchema = tool.InputSchema.GetRawText(),
            Parameters = ExtractParameters(tool.InputSchema)
        }).ToList();
    }

    /// <summary>
    /// Builds the outbound header set for a server: the admin-configured static headers,
    /// plus a freshly-minted <c>Authorization: Bearer</c> header when the server uses the
    /// OAuth client-credentials grant. The OAuth token takes precedence over any static
    /// Authorization header so the two auth modes never collide.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, string>> BuildRequestHeadersAsync(ExternalMcpServer server, CancellationToken ct)
    {
        var headers = new Dictionary<string, string>(ReadHeaders(server.HeadersJson), StringComparer.OrdinalIgnoreCase);

        if (string.Equals(server.AuthMode, McpOAuthTokenProvider.ClientCredentialsMode, StringComparison.OrdinalIgnoreCase))
        {
            var token = await _oauthTokenProvider.GetAccessTokenAsync(server, ct);
            headers["Authorization"] = $"Bearer {token}";
        }

        return headers;
    }

    private IReadOnlyDictionary<string, string> ReadHeaders(string? headersJson)
    {
        if (string.IsNullOrWhiteSpace(headersJson)) return new Dictionary<string, string>();
        
        try
        {
            var plaintextHeaders = _headerProtector.Unprotect(headersJson);
            return JsonSerializer.Deserialize<Dictionary<string, string>>(plaintextHeaders)
                ?? new Dictionary<string, string>();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "⚠️ [MCP] Failed to parse HeadersJson");
            return new Dictionary<string, string>();
        }
    }

    private static List<McpToolParameter> ExtractParameters(JsonElement inputSchema)
    {
        if (inputSchema.ValueKind != JsonValueKind.Object ||
            !inputSchema.TryGetProperty("properties", out var properties) ||
            properties.ValueKind != JsonValueKind.Object)
            return new List<McpToolParameter>();

        var required = inputSchema.TryGetProperty("required", out var requiredElement) && requiredElement.ValueKind == JsonValueKind.Array
            ? requiredElement.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString()!)
                .ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);

        return properties.EnumerateObject().Select(property =>
        {
            var schema = property.Value;
            var type = schema.ValueKind == JsonValueKind.Object && schema.TryGetProperty("type", out var typeElement)
                ? typeElement.ValueKind == JsonValueKind.String ? typeElement.GetString() ?? "object" : "object"
                : "object";
            var description = schema.ValueKind == JsonValueKind.Object && schema.TryGetProperty("description", out var descriptionElement)
                && descriptionElement.ValueKind == JsonValueKind.String
                ? descriptionElement.GetString()
                : null;
            return new McpToolParameter
            {
                Name = property.Name,
                Type = type,
                Description = description,
                Required = required.Contains(property.Name)
            };
        }).ToList();
    }
}

// ---- Models ----

public class McpToolDefinition
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string ServerName { get; set; } = string.Empty;
    public bool AllowAutomaticExecution { get; set; }
    public string? InputSchema { get; set; }
    public List<McpToolParameter> Parameters { get; set; } = new();
}

public class McpToolParameter
{
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = "string";
    public bool Required { get; set; }
    public string? Description { get; set; }
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
