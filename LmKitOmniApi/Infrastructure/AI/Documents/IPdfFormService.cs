namespace LmKitOmniApi.Infrastructure.AI.Documents;

/// <summary>One AcroForm field, flattened to the shape the SPA/agent consumes.</summary>
public sealed record PdfFormFieldDto(
    string Name,
    string Label,
    string Kind,
    string Value,
    IReadOnlyList<string> Options,
    bool IsRequired,
    bool IsReadOnly,
    int PageIndex);

/// <summary>The form snapshot for a PDF: whether it has an AcroForm and its fields.</summary>
public sealed record PdfFormFieldsDto(bool HasForm, IReadOnlyList<PdfFormFieldDto> Fields);

/// <summary>Outcome of a fill pass: how many fields were set/skipped, whether the form was flattened, and any per-field issues.</summary>
public sealed record PdfFormFillReportDto(
    int FieldsSet,
    int FieldsSkipped,
    bool Flattened,
    IReadOnlyList<string> Issues);

/// <summary>
/// Reads and fills AcroForm fields in a PDF via LM-Kit.NET's static
/// <c>LMKit.Document.Pdf.PdfForm</c>. A pure document API — no model, no network — so
/// the only safety surface is the enable gate plus the up-front input validation
/// (size cap, PDF magic bytes) applied before LM-Kit is touched. Reading fields is a
/// safe read; filling produces a derived PDF the controller persists into the
/// caller's isolated upload root.
/// </summary>
public interface IPdfFormService
{
    /// <summary>
    /// True only when an operator has enabled the document tools
    /// (DocumentTools:Enabled). When false every method throws
    /// <see cref="DocumentToolsDisabledException"/>; the controller checks this and
    /// returns 501 instead.
    /// </summary>
    bool IsEnabled { get; }

    /// <summary>
    /// Returns the form snapshot for <paramref name="pdfData"/>. A PDF with no
    /// AcroForm yields <c>HasForm=false</c> and an empty field list.
    /// </summary>
    /// <exception cref="DocumentToolsDisabledException">The feature is disabled.</exception>
    /// <exception cref="DocumentValidationException">Empty, over the size cap, or not a PDF.</exception>
    PdfFormFieldsDto GetFields(byte[] pdfData, CancellationToken ct = default);

    /// <summary>
    /// Sets the given field values and returns the derived PDF plus a fill report.
    /// When <paramref name="flatten"/> is true the form is flattened (fields baked in,
    /// no longer editable). Fields absent from the PDF are reported as skipped, not an
    /// error.
    /// </summary>
    /// <exception cref="DocumentToolsDisabledException">The feature is disabled.</exception>
    /// <exception cref="DocumentValidationException">Empty, over a size cap, or not a PDF.</exception>
    (byte[] Data, PdfFormFillReportDto Report) Fill(
        byte[] pdfData,
        IEnumerable<(string Name, string Value)> values,
        bool flatten,
        CancellationToken ct = default);
}
