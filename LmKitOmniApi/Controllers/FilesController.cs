using LmKitOmniApi.Infrastructure.AI.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.StaticFiles;

namespace LmKitOmniApi.Controllers;

/// <summary>
/// Serves files that live under the caller's isolated upload root — currently the
/// artefacts the code interpreter produced (e.g. a chart PNG or CSV from
/// run_python), referenced by the <c>[FILE:]</c> markers in a chat message.
///
/// Authentication is the app's standard cookie-borne JWT, so a plain
/// <c>&lt;img src="/api/files/{id}"&gt;</c> or <c>&lt;a download&gt;</c> in the SPA is
/// authenticated automatically (no bearer header needed). Ownership is enforced by
/// resolving the id ONLY within the (tenant, user) upload directory and validating
/// the result through <see cref="UserResourceAccessService"/>, so one user can
/// never read another's files and path traversal is impossible.
/// </summary>
[ApiController]
[Route("api/files")]
[Authorize]
public sealed class FilesController : ApiControllerBase
{
    private static readonly FileExtensionContentTypeProvider ContentTypeProvider = new();

    private readonly UserResourceAccessService _resources;

    public FilesController(UserResourceAccessService resources) => _resources = resources;

    [HttpGet("{id}")]
    public IActionResult Download(string id)
    {
        if (!TryGetIdentity(out var tenantId, out var userId)) return Unauthorized();

        // Never trust the id as a path: collapse it to a bare file name, then resolve
        // it strictly inside the caller's own upload directory.
        var safeName = Path.GetFileName(id);
        if (string.IsNullOrWhiteSpace(safeName)) return NotFound();

        var candidatePath = Path.Combine(_resources.GetUploadDirectory(tenantId, userId), safeName);
        var owned = _resources.ValidateOwnedPath(tenantId, userId, candidatePath);
        if (!owned.IsAllowed || !System.IO.File.Exists(owned.SanitizedPath))
            return NotFound();

        var contentType = ContentTypeProvider.TryGetContentType(owned.SanitizedPath, out var resolved)
            ? resolved
            : "application/octet-stream";

        // No fileDownloadName: images render inline via <img>; the SPA's <a download>
        // attribute supplies a friendly name when the user chooses to save.
        return PhysicalFile(owned.SanitizedPath, contentType, enableRangeProcessing: true);
    }
}
