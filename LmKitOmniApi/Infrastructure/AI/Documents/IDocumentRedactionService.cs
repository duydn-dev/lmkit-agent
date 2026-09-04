using LMKit.Document.Pdf;

namespace LmKitOmniApi.Infrastructure.AI.Documents;

/// <summary>Outcome of a PDF redaction pass.</summary>
public sealed record PdfRedactionReportDto(
    bool ContentRemoved,
    int SearchMatches,
    int RemovedGlyphs,
    int RemovedTextObjects,
    int RemovedImages,
    int PagesProcessed);

/// <summary>Outcome of an Office (OpenXML) redaction pass.</summary>
public sealed record OfficeRedactionReportDto(
    bool ContentRemoved,
    int PartsScanned,
    int ReplacedOccurrences);

/// <summary>One PDF/A conformance finding (a rule id + human description).</summary>
public sealed record PdfAFindingDto(string Rule, string Description);

/// <summary>PDF/A validation verdict for a document.</summary>
public sealed record PdfAValidationReportDto(
    string Verdict,
    string? Level,
    string? DeclaredConformance,
    int PageCount,
    int RulesEvaluated,
    IReadOnlyList<PdfAFindingDto> Findings);

/// <summary>
/// Redacts PDFs (<c>LMKit.Document.Pdf.PdfRedactor</c>) and Office/OpenXML documents
/// (<c>LMKit.Document.OpenXml.OfficeRedactor</c>), and validates PDF/A conformance
/// (<c>LMKit.Document.Pdf.PdfAValidator</c>). All pure document APIs — no model, no
/// network — so the safety surface is the enable gate plus up-front input validation
/// (size cap, magic bytes, term-count cap) applied before LM-Kit is touched.
/// Redaction removes the matched content from the byte stream (not a cosmetic
/// overlay) and returns a derived document the controller persists into the caller's
/// isolated upload root.
/// </summary>
public interface IDocumentRedactionService
{
    /// <summary>
    /// True only when an operator has enabled the document tools
    /// (DocumentTools:Enabled). When false every method throws
    /// <see cref="DocumentToolsDisabledException"/>; the controller checks this and
    /// returns 501 instead.
    /// </summary>
    bool IsEnabled { get; }

    /// <summary>
    /// Removes every occurrence of the given terms from <paramref name="pdfData"/> and
    /// returns the redacted PDF plus a report.
    /// </summary>
    /// <exception cref="DocumentToolsDisabledException">The feature is disabled.</exception>
    /// <exception cref="DocumentValidationException">Empty, over a size cap, not a PDF, or a bad term list.</exception>
    (byte[] Data, PdfRedactionReportDto Report) RedactPdf(
        byte[] pdfData,
        IEnumerable<string> searchTerms,
        bool caseSensitive,
        bool wholeWord,
        CancellationToken ct = default);

    /// <summary>
    /// Removes every occurrence of the given terms from an OpenXML document
    /// (<paramref name="extension"/> selects the format, e.g. ".docx") and returns the
    /// redacted document plus a report.
    /// </summary>
    /// <exception cref="DocumentToolsDisabledException">The feature is disabled.</exception>
    /// <exception cref="DocumentValidationException">Empty, over a size cap, not an OpenXML package, unsupported extension, or a bad term list.</exception>
    (byte[] Data, OfficeRedactionReportDto Report) RedactOffice(
        byte[] data,
        string extension,
        IEnumerable<string> searchTerms,
        bool caseSensitive,
        bool wholeWord,
        CancellationToken ct = default);

    /// <summary>
    /// Validates <paramref name="pdfData"/> for PDF/A conformance. When
    /// <paramref name="level"/> is null the validator infers the target level from the
    /// document's declared conformance.
    /// </summary>
    /// <exception cref="DocumentToolsDisabledException">The feature is disabled.</exception>
    /// <exception cref="DocumentValidationException">Empty, over the size cap, or not a PDF.</exception>
    PdfAValidationReportDto ValidatePdfA(
        byte[] pdfData,
        PdfAConformanceLevel? level,
        CancellationToken ct = default);
}
