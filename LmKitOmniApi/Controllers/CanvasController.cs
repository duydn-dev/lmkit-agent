using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using LmKitOmniApi.Application.Canvas.Commands;
using LmKitOmniApi.Application.Canvas.DTOs;
using LmKitOmniApi.Application.Canvas.Queries;
using LmKitOmniApi.Domain.Entities;

namespace LmKitOmniApi.Controllers;

/// <summary>
/// Versioned canvas artifacts (editable workspace documents). Saves are
/// append-only version rows sharing one root id; "the artifact" is always the
/// highest version of its root. Everything is tenant + user scoped in the
/// handlers, and ownership misses surface as 404 — never 403 — so foreign
/// roots are indistinguishable from missing ones. Validation errors follow the
/// codebase's Vietnamese <c>{ message }</c> convention.
/// </summary>
[ApiController]
[Route("api/canvas")]
[Authorize]
public sealed class CanvasController : ApiControllerBase
{
    // Caps mirror the CanvasArtifact column limits and the fixed API contract.
    private const int TitleMaxLength = CanvasArtifact.TitleMaxLength;
    private const int LanguageMaxLength = 40;
    private const int ContentMaxLength = 200_000;
    private static readonly string[] AllowedKinds = ["markdown", "code", "text"];

    private readonly IMediator _mediator;

    public CanvasController(IMediator mediator)
    {
        _mediator = mediator;
    }

    /// <summary>
    /// Latest version of each of the caller's artifacts, newest-updated first,
    /// capped at 100. <paramref name="sessionId"/> narrows to one chat session.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] Guid? sessionId, CancellationToken cancellationToken)
    {
        if (!TryGetIdentity(out var tenantId, out var userId)) return Unauthorized();

        var query = new ListCanvasArtifactsQuery
        {
            TenantId = tenantId,
            UserId = userId,
            SessionId = sessionId
        };
        var artifacts = await _mediator.Send(query, cancellationToken);
        return Ok(artifacts);
    }

    /// <summary>
    /// One owned artifact: the latest version by default, or the exact
    /// <paramref name="version"/> when supplied. 404 for unknown roots,
    /// foreign roots, and unknown versions alike.
    /// </summary>
    [HttpGet("{rootId:guid}")]
    public async Task<IActionResult> Get(Guid rootId, [FromQuery] int? version, CancellationToken cancellationToken)
    {
        if (!TryGetIdentity(out var tenantId, out var userId)) return Unauthorized();

        var query = new GetCanvasArtifactQuery
        {
            TenantId = tenantId,
            UserId = userId,
            RootId = rootId,
            Version = version
        };
        var artifact = await _mediator.Send(query, cancellationToken);
        return artifact is null ? NotFound() : Ok(artifact);
    }

    /// <summary>Version history of one owned root, newest first. 404 when the root is unknown.</summary>
    [HttpGet("{rootId:guid}/versions")]
    public async Task<IActionResult> ListVersions(Guid rootId, CancellationToken cancellationToken)
    {
        if (!TryGetIdentity(out var tenantId, out var userId)) return Unauthorized();

        var query = new ListCanvasVersionsQuery
        {
            TenantId = tenantId,
            UserId = userId,
            RootId = rootId
        };
        var versions = await _mediator.Send(query, cancellationToken);
        return versions is null ? NotFound() : Ok(versions);
    }

    /// <summary>
    /// Create a new artifact as version 1 of a fresh root. Returns 201 with the
    /// full latest-shape payload and a Location pointing at the new root.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateCanvasArtifactRequest request, CancellationToken cancellationToken)
    {
        if (!TryGetIdentity(out var tenantId, out var userId)) return Unauthorized();

        var title = request.Title?.Trim();
        if (string.IsNullOrWhiteSpace(title))
            return BadRequest(new { message = "Tiêu đề không được để trống." });
        if (title.Length > TitleMaxLength)
            return BadRequest(new { message = $"Tiêu đề không được vượt quá {TitleMaxLength} ký tự." });

        var kind = request.Kind?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(kind) || !AllowedKinds.Contains(kind))
            return BadRequest(new { message = "Loại canvas không hợp lệ (markdown, code hoặc text)." });

        var language = request.Language?.Trim();
        if (string.IsNullOrEmpty(language)) language = null;
        if (language is { Length: > LanguageMaxLength })
            return BadRequest(new { message = $"Ngôn ngữ không được vượt quá {LanguageMaxLength} ký tự." });

        var contentError = ValidateContent(request.Content);
        if (contentError is not null)
            return BadRequest(new { message = contentError });

        var command = new CreateCanvasArtifactCommand
        {
            TenantId = tenantId,
            UserId = userId,
            ChatSessionId = request.ChatSessionId,
            Title = title,
            Kind = kind,
            Language = language,
            Content = request.Content!
        };
        var created = await _mediator.Send(command, cancellationToken);

        // The handler's only null: a chatSessionId that is not the caller's own
        // session. Kept a 400 (not 404) to preserve POST semantics — the fixed
        // contract's exact message, matched verbatim by the frontend.
        if (created is null)
            return BadRequest(new { message = "Phiên chat không hợp lệ" });

        return CreatedAtAction(nameof(Get), new { rootId = created.RootId }, created);
    }

    /// <summary>
    /// Save the artifact: appends a new version row (never mutates old ones).
    /// Omitted/blank title keeps the previous title. Returns the new row's
    /// <c>{ id, version }</c>; 404 when the root is unknown or foreign.
    /// </summary>
    [HttpPut("{rootId:guid}")]
    public async Task<IActionResult> Update(Guid rootId, [FromBody] UpdateCanvasArtifactRequest request, CancellationToken cancellationToken)
    {
        if (!TryGetIdentity(out var tenantId, out var userId)) return Unauthorized();

        // Blank title counts as omitted: the handler carries the previous title forward.
        var title = request.Title?.Trim();
        if (string.IsNullOrWhiteSpace(title)) title = null;
        if (title is { Length: > TitleMaxLength })
            return BadRequest(new { message = $"Tiêu đề không được vượt quá {TitleMaxLength} ký tự." });

        var contentError = ValidateContent(request.Content);
        if (contentError is not null)
            return BadRequest(new { message = contentError });

        var command = new UpdateCanvasArtifactCommand
        {
            TenantId = tenantId,
            UserId = userId,
            RootId = rootId,
            Title = title,
            Content = request.Content!
        };
        var updated = await _mediator.Send(command, cancellationToken);
        return updated is null ? NotFound() : Ok(updated);
    }

    /// <summary>Delete an owned artifact including its whole version history. 404 when unknown.</summary>
    [HttpDelete("{rootId:guid}")]
    public async Task<IActionResult> Delete(Guid rootId, CancellationToken cancellationToken)
    {
        if (!TryGetIdentity(out var tenantId, out var userId)) return Unauthorized();

        var command = new DeleteCanvasArtifactCommand
        {
            TenantId = tenantId,
            UserId = userId,
            RootId = rootId
        };
        var deleted = await _mediator.Send(command, cancellationToken);
        return deleted ? NoContent() : NotFound();
    }

    /// <summary>Shared POST/PUT content rule: required, at most 200,000 characters.</summary>
    private static string? ValidateContent(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return "Nội dung không được để trống.";
        if (content.Length > ContentMaxLength)
            return "Nội dung không được vượt quá 200.000 ký tự.";
        return null;
    }
}
