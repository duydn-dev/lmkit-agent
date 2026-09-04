namespace LmKitOmniApi.Infrastructure.AI.Documents;

/// <summary>
/// Configuration for the native document tools — PDF form read/fill
/// (<see cref="IPdfFormService"/>) and PDF/Office redaction + PDF/A validation
/// (<see cref="IDocumentRedactionService"/>). Bound from the "DocumentTools"
/// configuration section.
///
/// DISABLED BY DEFAULT: these operate on user-uploaded documents and write derived
/// files back into the caller's isolated upload root, so they only run when an
/// operator explicitly enables them. When disabled the services report
/// <c>IsEnabled=false</c> and every operation throws
/// <see cref="DocumentToolsDisabledException"/> — the controller surfaces that as a
/// 501 (feature off), the same gating shape as the Python/browser/web-read tools.
///
/// Unlike those tools these are PURE document APIs (LM-Kit.NET's PdfForm /
/// PdfRedactor / OfficeRedactor / PdfAValidator) with no model, no network egress
/// and no container — so the only safety surface is input validation (size caps,
/// magic-byte sniffing, term-count caps) and strict owner-scoped output, all applied
/// BEFORE LM-Kit is ever touched.
/// </summary>
public sealed class DocumentToolsOptions
{
    public const string SectionName = "DocumentTools";

    /// <summary>Master switch. False (default) = every document tool is off.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Hard cap on an uploaded document, in bytes. Enforced by the controller on the
    /// raw upload AND again by the services (defense-in-depth) before LM-Kit is
    /// invoked. Default 25 MB.
    /// </summary>
    public long MaxInputBytes { get; set; } = 25L * 1024 * 1024;

    /// <summary>
    /// Cap on the number of search terms accepted by a single redaction request — a
    /// bound on the work a redaction pass performs. Default 50.
    /// </summary>
    public int MaxSearchTerms { get; set; } = 50;

    /// <summary>
    /// Hard cap on the produced (filled/redacted) document persisted for the caller,
    /// in bytes. A produced file larger than this is refused rather than written.
    /// Default 25 MB.
    /// </summary>
    public long MaxOutputBytes { get; set; } = 25L * 1024 * 1024;
}
