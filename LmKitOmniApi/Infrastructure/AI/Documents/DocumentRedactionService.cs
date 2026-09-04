using LMKit.Document.OpenXml;
using LMKit.Document.Pdf;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LmKitOmniApi.Infrastructure.AI.Documents;

/// <summary>
/// Default <see cref="IDocumentRedactionService"/>. Thin, hermetically-testable
/// wrapper over LM-Kit.NET's static <c>PdfRedactor</c>, <c>OfficeRedactor</c> and
/// <c>PdfAValidator</c>: it enforces the enable gate and the up-front input
/// validation (size cap, magic bytes, term-count cap) and maps LM-Kit's result types
/// onto the transport DTOs. No model, no network — everything here runs offline.
/// </summary>
public sealed class DocumentRedactionService : IDocumentRedactionService
{
    private readonly DocumentToolsOptions _options;
    private readonly ILogger<DocumentRedactionService> _logger;

    public DocumentRedactionService(IOptions<DocumentToolsOptions> options, ILogger<DocumentRedactionService> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public bool IsEnabled => _options.Enabled;

    public (byte[] Data, PdfRedactionReportDto Report) RedactPdf(
        byte[] pdfData,
        IEnumerable<string> searchTerms,
        bool caseSensitive,
        bool wholeWord,
        CancellationToken ct = default)
    {
        DocumentInputValidator.EnsureEnabled(_options.Enabled);
        DocumentInputValidator.EnsureWithinInputLimit(pdfData, _options.MaxInputBytes);
        DocumentInputValidator.EnsurePdf(pdfData);
        var terms = DocumentInputValidator.EnsureSearchTerms(searchTerms, _options.MaxSearchTerms);

        var request = new PdfRedactionRequest
        {
            CaseSensitiveSearch = caseSensitive,
            WholeWordSearch = wholeWord,
        };
        request.SearchTerms.AddRange(terms);

        var result = PdfRedactor.RedactToBytes(pdfData, request, cancellationToken: ct);

        var data = result.Data ?? Array.Empty<byte>();
        DocumentInputValidator.EnsureWithinOutputLimit(data, _options.MaxOutputBytes);

        var report = result.Report;
        _logger.LogInformation(
            "🖊️ [PdfRedact] {Matches} match(es), {Glyphs} glyph(s) removed across {Pages} page(s) (ContentRemoved={Removed}).",
            report?.SearchMatches ?? 0, report?.RemovedGlyphs ?? 0, report?.PagesProcessed ?? 0, report?.ContentRemoved ?? false);

        return (data, new PdfRedactionReportDto(
            ContentRemoved: report?.ContentRemoved ?? false,
            SearchMatches: report?.SearchMatches ?? 0,
            RemovedGlyphs: report?.RemovedGlyphs ?? 0,
            RemovedTextObjects: report?.RemovedTextObjects ?? 0,
            RemovedImages: report?.RemovedImages ?? 0,
            PagesProcessed: report?.PagesProcessed ?? 0));
    }

    public (byte[] Data, OfficeRedactionReportDto Report) RedactOffice(
        byte[] data,
        string extension,
        IEnumerable<string> searchTerms,
        bool caseSensitive,
        bool wholeWord,
        CancellationToken ct = default)
    {
        DocumentInputValidator.EnsureEnabled(_options.Enabled);
        DocumentInputValidator.EnsureWithinInputLimit(data, _options.MaxInputBytes);
        var ext = DocumentInputValidator.EnsureOpenXml(data, extension);
        var terms = DocumentInputValidator.EnsureSearchTerms(searchTerms, _options.MaxSearchTerms);

        var request = new OfficeRedactionRequest
        {
            CaseSensitiveSearch = caseSensitive,
            WholeWordSearch = wholeWord,
        };
        request.SearchTerms.AddRange(terms);

        var result = OfficeRedactor.RedactToBytes(data, ext, request, cancellationToken: ct);

        var outputData = result.Data ?? Array.Empty<byte>();
        DocumentInputValidator.EnsureWithinOutputLimit(outputData, _options.MaxOutputBytes);

        var report = result.Report;
        _logger.LogInformation(
            "🖊️ [OfficeRedact] {Replaced} occurrence(s) replaced across {Parts} part(s) (ContentRemoved={Removed}).",
            report?.ReplacedOccurrences ?? 0, report?.PartsScanned ?? 0, report?.ContentRemoved ?? false);

        return (outputData, new OfficeRedactionReportDto(
            ContentRemoved: report?.ContentRemoved ?? false,
            PartsScanned: report?.PartsScanned ?? 0,
            ReplacedOccurrences: report?.ReplacedOccurrences ?? 0));
    }

    public PdfAValidationReportDto ValidatePdfA(byte[] pdfData, PdfAConformanceLevel? level, CancellationToken ct = default)
    {
        DocumentInputValidator.EnsureEnabled(_options.Enabled);
        DocumentInputValidator.EnsureWithinInputLimit(pdfData, _options.MaxInputBytes);
        DocumentInputValidator.EnsurePdf(pdfData);

        var options = level is null ? null : new PdfAValidationOptions { Level = level };
        var report = PdfAValidator.Validate(pdfData, options, ct);

        var findings = (report.Findings ?? Array.Empty<PdfAValidationFinding>())
            .Select(finding => new PdfAFindingDto(finding.Rule ?? string.Empty, finding.Description ?? string.Empty))
            .ToList();

        _logger.LogInformation(
            "🧾 [PdfA] Verdict={Verdict}, Level={Level}, {Findings} finding(s) over {Rules} rule(s).",
            report.Verdict, report.Level, findings.Count, report.RulesEvaluated);

        return new PdfAValidationReportDto(
            Verdict: report.Verdict.ToString(),
            Level: report.Level?.ToString(),
            DeclaredConformance: report.DeclaredConformance,
            PageCount: report.PageCount,
            RulesEvaluated: report.RulesEvaluated,
            Findings: findings);
    }
}
