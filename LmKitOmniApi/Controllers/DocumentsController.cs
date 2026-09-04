using System.Text.Json;
using LMKit.Document.Pdf;
using LmKitOmniApi.Infrastructure.AI.Documents;
using LmKitOmniApi.Infrastructure.AI.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace LmKitOmniApi.Controllers;

/// <summary>
/// Native document tools: PDF form read/fill, PDF + Office redaction, and PDF/A
/// validation. Presentation-only — multipart/IFormFile handling, identity parsing,
/// size gating and owner-scoped output. The document work lives in
/// <see cref="IPdfFormService"/> and <see cref="IDocumentRedactionService"/> (pure
/// LM-Kit.NET document APIs; no model, no network).
///
/// DISABLED BY DEFAULT (DocumentTools:Enabled). When off, every endpoint returns 501
/// (feature off). Uploads are capped at <see cref="DocumentToolsOptions.MaxInputBytes"/>
/// and are never trusted by extension alone — the services sniff magic bytes before
/// invoking LM-Kit. Produced (filled/redacted) files are written ONLY into the
/// caller's isolated upload root (via <see cref="UserResourceAccessService"/>) and
/// returned by id for download through <c>/api/files/{id}</c>; a client-supplied path
/// is never accepted.
/// </summary>
[ApiController]
[Route("api/documents")]
[Authorize]
public sealed class DocumentsController : ApiControllerBase
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly IPdfFormService _forms;
    private readonly IDocumentRedactionService _redaction;
    private readonly UserResourceAccessService _resources;
    private readonly DocumentToolsOptions _options;
    private readonly ILogger<DocumentsController> _logger;

    public DocumentsController(
        IPdfFormService forms,
        IDocumentRedactionService redaction,
        UserResourceAccessService resources,
        IOptions<DocumentToolsOptions> options,
        ILogger<DocumentsController> logger)
    {
        _forms = forms;
        _redaction = redaction;
        _resources = resources;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>Reads the AcroForm fields of an uploaded PDF (a safe read; no output produced).</summary>
    [HttpPost("pdf/form/fields")]
    public async Task<IActionResult> GetPdfFormFields(IFormFile file, CancellationToken ct)
    {
        if (!_forms.IsEnabled) return FeatureOff();
        if (ReadUpload(file, ct) is not { } read) return UploadError();
        var bytes = await read;

        return Execute(() => Ok(_forms.GetFields(bytes, ct)));
    }

    /// <summary>Fills PDF form fields and writes the derived PDF into the caller's upload root.</summary>
    [HttpPost("pdf/form/fill")]
    public async Task<IActionResult> FillPdfForm(
        IFormFile file,
        [FromForm] string? values,
        [FromForm] bool flatten,
        CancellationToken ct)
    {
        if (!_forms.IsEnabled) return FeatureOff();
        if (!TryGetIdentity(out var tenantId, out var userId)) return Unauthorized();
        if (ReadUpload(file, ct) is not { } read) return UploadError();
        var bytes = await read;

        List<(string Name, string Value)> parsed;
        try { parsed = ParseFieldValues(values); }
        catch (DocumentValidationException ex) { return BadRequest(new { message = ex.Message }); }

        return await ExecuteAsync(async () =>
        {
            var (data, report) = _forms.Fill(bytes, parsed, flatten, ct);
            var (fileId, name) = await PersistAsync(tenantId, userId, data, ".pdf", "filled", file.FileName, ct);
            return Ok(new { fileId, name, report });
        });
    }

    /// <summary>Redacts search terms from an uploaded PDF and writes the redacted PDF into the caller's upload root.</summary>
    [HttpPost("pdf/redact")]
    public async Task<IActionResult> RedactPdf(
        IFormFile file,
        [FromForm] string? terms,
        [FromForm] bool caseSensitive,
        [FromForm] bool wholeWord,
        CancellationToken ct)
    {
        if (!_redaction.IsEnabled) return FeatureOff();
        if (!TryGetIdentity(out var tenantId, out var userId)) return Unauthorized();
        if (ReadUpload(file, ct) is not { } read) return UploadError();
        var bytes = await read;

        var searchTerms = ParseTerms(terms);

        return await ExecuteAsync(async () =>
        {
            var (data, report) = _redaction.RedactPdf(bytes, searchTerms, caseSensitive, wholeWord, ct);
            var (fileId, name) = await PersistAsync(tenantId, userId, data, ".pdf", "redacted", file.FileName, ct);
            return Ok(new { fileId, name, report });
        });
    }

    /// <summary>Redacts search terms from an uploaded Office (OpenXML) document and writes the result into the caller's upload root.</summary>
    [HttpPost("office/redact")]
    public async Task<IActionResult> RedactOffice(
        IFormFile file,
        [FromForm] string? terms,
        [FromForm] bool caseSensitive,
        [FromForm] bool wholeWord,
        CancellationToken ct)
    {
        if (!_redaction.IsEnabled) return FeatureOff();
        if (!TryGetIdentity(out var tenantId, out var userId)) return Unauthorized();
        if (ReadUpload(file, ct) is not { } read) return UploadError();
        var bytes = await read;

        var searchTerms = ParseTerms(terms);
        var ext = Path.GetExtension(file.FileName);

        return await ExecuteAsync(async () =>
        {
            var (data, report) = _redaction.RedactOffice(bytes, ext, searchTerms, caseSensitive, wholeWord, ct);
            var outExt = string.IsNullOrWhiteSpace(ext) ? ".docx" : ext.ToLowerInvariant();
            var (fileId, name) = await PersistAsync(tenantId, userId, data, outExt, "redacted", file.FileName, ct);
            return Ok(new { fileId, name, report });
        });
    }

    /// <summary>Validates an uploaded PDF for PDF/A conformance (a safe read; no output produced).</summary>
    [HttpPost("pdf-a/validate")]
    public async Task<IActionResult> ValidatePdfA(
        IFormFile file,
        [FromForm] string? level,
        CancellationToken ct)
    {
        if (!_redaction.IsEnabled) return FeatureOff();
        if (ReadUpload(file, ct) is not { } read) return UploadError();
        var bytes = await read;

        PdfAConformanceLevel? parsedLevel = null;
        if (!string.IsNullOrWhiteSpace(level))
        {
            if (!Enum.TryParse<PdfAConformanceLevel>(level, ignoreCase: true, out var value))
                return BadRequest(new { message = $"Unknown PDF/A level '{level}'. Expected one of: {string.Join(", ", Enum.GetNames<PdfAConformanceLevel>())}." });
            parsedLevel = value;
        }

        return Execute(() => Ok(_redaction.ValidatePdfA(bytes, parsedLevel, ct)));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Validates the raw upload (present, non-empty, within the declared size cap) and
    /// returns a task that reads it to a byte array. Returns null when the upload is
    /// missing/empty/over-limit, in which case <see cref="UploadError"/> is the reply.
    /// </summary>
    private Task<byte[]>? ReadUpload(IFormFile file, CancellationToken ct)
    {
        if (file is null || file.Length == 0) return null;
        if (file.Length > _options.MaxInputBytes) return null;
        return ReadAllBytesAsync(file, ct);
    }

    private static async Task<byte[]> ReadAllBytesAsync(IFormFile file, CancellationToken ct)
    {
        await using var stream = file.OpenReadStream();
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, ct);
        return buffer.ToArray();
    }

    private IActionResult UploadError() =>
        BadRequest(new { message = $"No file uploaded, or the file exceeds the {_options.MaxInputBytes}-byte limit." });

    private IActionResult FeatureOff() =>
        StatusCode(StatusCodes.Status501NotImplemented, new { message = "The document tools feature is disabled." });

    /// <summary>Runs a synchronous service call, mapping the document exceptions onto HTTP status codes.</summary>
    private IActionResult Execute(Func<IActionResult> action)
    {
        try { return action(); }
        catch (DocumentToolsDisabledException) { return FeatureOff(); }
        catch (DocumentValidationException ex) { return BadRequest(new { message = ex.Message }); }
        catch (OperationCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Document operation failed.");
            return Problem(statusCode: 500, title: "Document operation failed.");
        }
    }

    /// <summary>Async counterpart to <see cref="Execute"/>.</summary>
    private async Task<IActionResult> ExecuteAsync(Func<Task<IActionResult>> action)
    {
        try { return await action(); }
        catch (DocumentToolsDisabledException) { return FeatureOff(); }
        catch (DocumentValidationException ex) { return BadRequest(new { message = ex.Message }); }
        catch (OperationCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Document operation failed.");
            return Problem(statusCode: 500, title: "Document operation failed.");
        }
    }

    /// <summary>
    /// Persists a produced document into the caller's isolated upload root under a
    /// server-generated name and returns its id (the on-disk name, downloadable via
    /// <c>/api/files/{id}</c>) plus a friendly display name derived from the original
    /// upload. The client filename is never used for the on-disk path.
    /// </summary>
    private async Task<(string FileId, string Name)> PersistAsync(
        Guid tenantId, Guid userId, byte[] data, string outputExtension, string prefix, string originalName, CancellationToken ct)
    {
        var uploadDir = _resources.GetUploadDirectory(tenantId, userId);
        Directory.CreateDirectory(uploadDir);

        var storedName = $"{Guid.NewGuid():N}{outputExtension}";
        var absolutePath = Path.Combine(uploadDir, storedName);

        // Defense-in-depth: the path is built inside the owned dir, but validate it anyway.
        var owned = _resources.ValidateOwnedPath(tenantId, userId, absolutePath);
        if (!owned.IsAllowed)
            throw new InvalidOperationException("Failed to resolve an owner-scoped output path.");

        await System.IO.File.WriteAllBytesAsync(owned.SanitizedPath, data, ct);

        var safeOriginal = Path.GetFileName(originalName);
        if (string.IsNullOrWhiteSpace(safeOriginal)) safeOriginal = $"document{outputExtension}";
        return (storedName, $"{prefix}-{safeOriginal}");
    }

    /// <summary>
    /// Parses the form fill values. Accepts a JSON array of {name,value} objects. A
    /// null/blank value means "no values" (a valid flatten-only request).
    /// </summary>
    private static List<(string Name, string Value)> ParseFieldValues(string? values)
    {
        if (string.IsNullOrWhiteSpace(values)) return new List<(string, string)>();

        List<FieldValueInput>? parsed;
        try { parsed = JsonSerializer.Deserialize<List<FieldValueInput>>(values, JsonOptions); }
        catch (JsonException) { throw new DocumentValidationException("The 'values' field must be a JSON array of {name, value} objects."); }

        return (parsed ?? new List<FieldValueInput>())
            .Where(item => !string.IsNullOrEmpty(item.Name))
            .Select(item => (item.Name!, item.Value ?? string.Empty))
            .ToList();
    }

    /// <summary>
    /// Parses the redaction terms. Accepts either a JSON array of strings or, as a
    /// convenience, a newline-separated list. Blank entries are dropped; the service
    /// enforces the count cap and the "at least one term" rule.
    /// </summary>
    private static List<string> ParseTerms(string? terms)
    {
        if (string.IsNullOrWhiteSpace(terms)) return new List<string>();

        var trimmed = terms.TrimStart();
        if (trimmed.StartsWith('['))
        {
            try
            {
                var parsed = JsonSerializer.Deserialize<List<string>>(terms, JsonOptions);
                if (parsed is not null) return parsed;
            }
            catch (JsonException) { /* fall through to newline splitting */ }
        }

        return terms
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
    }

    private sealed record FieldValueInput(string? Name, string? Value);
}
