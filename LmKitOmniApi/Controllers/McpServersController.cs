using LmKitOmniApi.Application.McpServers;
using LmKitOmniApi.Application.McpServers.Commands;
using LmKitOmniApi.Application.McpServers.Queries;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LmKitOmniApi.Controllers;

[ApiController]
[Route("api/mcp-servers")]
[Authorize(Roles = "Admin")]
public sealed class McpServersController : ApiControllerBase
{
    private readonly IMediator _mediator;

    public McpServersController(IMediator mediator)
    {
        _mediator = mediator;
    }

    /// <summary>
    /// GET /api/mcp-servers/catalog — danh sách tĩnh các MCP server công khai được
    /// đề xuất (xem <see cref="McpServerCatalog"/>). Chỉ là gợi ý: admin vẫn phải
    /// thêm và xác thực từng server qua flow POST /api/mcp-servers hiện có (sandbox
    /// SSRF, mã hóa header, trust-policy). Không gọi network. Kết hợp với
    /// [Authorize(Roles = "Admin")] ở cấp class, endpoint vẫn chỉ dành cho Admin —
    /// [Authorize] ở đây kéo default policy (JWT + ApiKey) vào để cả hai scheme
    /// đều dùng được.
    /// </summary>
    [HttpGet("catalog")]
    [Authorize]
    public IActionResult Catalog() => Ok(McpServerCatalog.Entries);

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        if (!TryGetTenantId(out var tenantId)) return Unauthorized();

        var servers = await _mediator.Send(new ListMcpServersQuery { TenantId = tenantId }, ct);
        return Ok(servers);
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] SaveMcpServerRequest request, CancellationToken ct)
    {
        if (!TryGetTenantId(out var tenantId)) return Unauthorized();

        var result = await _mediator.Send(new CreateMcpServerCommand
        {
            TenantId = tenantId,
            Name = request.Name,
            Url = request.Url,
            Headers = request.Headers,
            ReplaceHeaders = request.ReplaceHeaders,
            IsActive = request.IsActive,
            TrustReadOnlyAnnotations = request.TrustReadOnlyAnnotations
        }, ct);

        return result.Status switch
        {
            McpServerMutationStatus.ValidationFailed => BadRequest(result.ErrorMessage),
            McpServerMutationStatus.NameConflict => Conflict(result.ErrorMessage),
            _ => CreatedAtAction(nameof(List), new { id = result.Server!.Id }, result.Server)
        };
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] SaveMcpServerRequest request, CancellationToken ct)
    {
        if (!TryGetTenantId(out var tenantId)) return Unauthorized();

        var result = await _mediator.Send(new UpdateMcpServerCommand
        {
            TenantId = tenantId,
            ServerId = id,
            Name = request.Name,
            Url = request.Url,
            Headers = request.Headers,
            ReplaceHeaders = request.ReplaceHeaders,
            IsActive = request.IsActive,
            TrustReadOnlyAnnotations = request.TrustReadOnlyAnnotations
        }, ct);

        return result.Status switch
        {
            McpServerMutationStatus.NotFound => NotFound(),
            McpServerMutationStatus.ValidationFailed => BadRequest(result.ErrorMessage),
            McpServerMutationStatus.NameConflict => Conflict(result.ErrorMessage),
            _ => NoContent()
        };
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        if (!TryGetTenantId(out var tenantId)) return Unauthorized();

        var deleted = await _mediator.Send(new DeleteMcpServerCommand { TenantId = tenantId, ServerId = id }, ct);
        if (!deleted) return NotFound();
        return NoContent();
    }
}
