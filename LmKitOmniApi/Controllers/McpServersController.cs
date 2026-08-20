using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using LmKitOmniApi.Domain.Entities;
using LmKitOmniApi.Infrastructure.AI.Mcp;
using LmKitOmniApi.Infrastructure.AI.Security;
using LmKitOmniApi.Infrastructure.Data;
using LmKitOmniApi.Infrastructure.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LmKitOmniApi.Controllers;

[ApiController]
[Route("api/mcp-servers")]
[Authorize(Roles = "Admin")]
public sealed class McpServersController : ControllerBase
{
    private static readonly Regex ValidName = new("^[a-zA-Z0-9][a-zA-Z0-9_-]{1,63}$", RegexOptions.Compiled);
    private static readonly HashSet<string> BlockedHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host", "Content-Length", "Connection", "Transfer-Encoding", "X-Forwarded-For", "X-Forwarded-Host", "Forwarded"
    };
    private readonly HermesDbContext _db;
    private readonly ToolSandboxService _sandbox;
    private readonly McpHeaderProtector _protector;
    private readonly McpClientService _mcp;

    public McpServersController(HermesDbContext db, ToolSandboxService sandbox, McpHeaderProtector protector, McpClientService mcp)
    {
        _db = db;
        _sandbox = sandbox;
        _protector = protector;
        _mcp = mcp;
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        if (!TryGetTenantId(out var tenantId)) return Unauthorized();
        var servers = await _db.ExternalMcpServers
            .Where(server => server.TenantId == tenantId)
            .OrderBy(server => server.Name)
            .Select(server => new
            {
                server.Id, server.Name, server.Url, server.IsActive, server.TrustReadOnlyAnnotations,
                HasHeaders = server.HeadersJson != null,
                server.CreatedAtUtc, server.UpdatedAtUtc
            })
            .ToListAsync(ct);
        return Ok(servers);
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] SaveMcpServerRequest request, CancellationToken ct)
    {
        if (!TryGetTenantId(out var tenantId)) return Unauthorized();
        var validation = await ValidateRequestAsync(tenantId, null, request, ct);
        if (validation is not null) return validation;

        var server = new ExternalMcpServer
        {
            TenantId = tenantId,
            Name = request.Name.Trim().ToLowerInvariant(),
            Url = request.Url.TrimEnd('/'),
            HeadersJson = ProtectHeaders(request.Headers),
            IsActive = request.IsActive,
            TrustReadOnlyAnnotations = request.TrustReadOnlyAnnotations
        };
        _db.ExternalMcpServers.Add(server);
        await _db.SaveChangesAsync(ct);
        await _mcp.InvalidateTenantCacheAsync(tenantId, ct);
        return CreatedAtAction(nameof(List), new { id = server.Id }, new { server.Id, server.Name, server.Url, server.IsActive, server.TrustReadOnlyAnnotations });
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] SaveMcpServerRequest request, CancellationToken ct)
    {
        if (!TryGetTenantId(out var tenantId)) return Unauthorized();
        var server = await _db.ExternalMcpServers.FirstOrDefaultAsync(item => item.Id == id && item.TenantId == tenantId, ct);
        if (server is null) return NotFound();
        var validation = await ValidateRequestAsync(tenantId, id, request, ct);
        if (validation is not null) return validation;

        server.Name = request.Name.Trim().ToLowerInvariant();
        server.Url = request.Url.TrimEnd('/');
        server.IsActive = request.IsActive;
        server.TrustReadOnlyAnnotations = request.TrustReadOnlyAnnotations;
        if (request.ReplaceHeaders) server.HeadersJson = ProtectHeaders(request.Headers);
        server.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        await _mcp.InvalidateTenantCacheAsync(tenantId, ct);
        return NoContent();
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        if (!TryGetTenantId(out var tenantId)) return Unauthorized();
        var deleted = await _db.ExternalMcpServers
            .Where(server => server.Id == id && server.TenantId == tenantId)
            .ExecuteDeleteAsync(ct);
        if (deleted == 0) return NotFound();
        await _mcp.InvalidateTenantCacheAsync(tenantId, ct);
        return NoContent();
    }

    private async Task<IActionResult?> ValidateRequestAsync(Guid tenantId, Guid? id, SaveMcpServerRequest request, CancellationToken ct)
    {
        var normalizedName = request.Name?.Trim() ?? string.Empty;
        if (!ValidName.IsMatch(normalizedName))
            return BadRequest("Name must contain 2-64 letters, digits, underscores or hyphens.");
        if (!Uri.TryCreate(request.Url, UriKind.Absolute, out _)) return BadRequest("A valid absolute URL is required.");
        var url = await _sandbox.ValidateUrlAsync(request.Url, ct);
        if (!url.IsAllowed) return BadRequest(url.DenialReason);
        var comparableName = normalizedName.ToLower();
        if (await _db.ExternalMcpServers.AnyAsync(server => server.TenantId == tenantId && server.Name.ToLower() == comparableName && server.Id != id, ct))
            return Conflict("An MCP server with this name already exists.");
        if (request.Headers?.Count > 20 || request.Headers?.Any(header => BlockedHeaders.Contains(header.Key) || header.Key.Length > 100 || header.Value is null || header.Value.Length > 2_000) == true)
            return BadRequest("MCP headers exceeded the allowed limits or contained a blocked header.");
        return null;
    }

    private string? ProtectHeaders(Dictionary<string, string>? headers)
    {
        if (headers is null || headers.Count == 0) return null;
        return _protector.Protect(JsonSerializer.Serialize(headers));
    }

    private bool TryGetTenantId(out Guid tenantId) =>
        Guid.TryParse(User.FindFirst("TenantId")?.Value, out tenantId);
}

public sealed class SaveMcpServerRequest
{
    public string Name { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public Dictionary<string, string>? Headers { get; set; }
    public bool ReplaceHeaders { get; set; }
    public bool IsActive { get; set; } = true;
    public bool TrustReadOnlyAnnotations { get; set; }
}
