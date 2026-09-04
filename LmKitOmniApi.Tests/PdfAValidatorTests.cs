using LMKit.Document.Pdf;
using LmKitOmniApi.Infrastructure.AI.Documents;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LmKitOmniApi.Tests;

/// <summary>
/// Unit tests for the PDF/A validation path of <see cref="DocumentRedactionService"/>
/// (LM-Kit.NET's PdfAValidator). Gating/validation tests are hermetic; the real-API
/// test validates a MarkdownToPdf-generated PDF and asserts a well-formed report (a
/// verdict from the enum, a non-null findings list), which is the deterministic,
/// model-free contract regardless of whether the plain PDF happens to conform.
/// </summary>
public sealed class PdfAValidatorTests
{
    private static readonly byte[] FakePdf = "%PDF-1.7 not-a-real-pdf"u8.ToArray();

    private static DocumentRedactionService Create(bool enabled = true) =>
        new(Options.Create(new DocumentToolsOptions
        {
            Enabled = enabled,
            MaxInputBytes = 25L * 1024 * 1024,
            MaxSearchTerms = 50,
            MaxOutputBytes = 25L * 1024 * 1024,
        }), NullLogger<DocumentRedactionService>.Instance);

    [Fact]
    public void Disabled_Throws()
    {
        Assert.Throws<DocumentToolsDisabledException>(() => Create(enabled: false).ValidatePdfA(FakePdf, level: null));
    }

    [Fact]
    public void NonPdf_Throws()
    {
        Assert.Throws<DocumentValidationException>(() => Create().ValidatePdfA(new byte[] { 1, 2, 3, 4 }, level: null));
    }

    [SkippableFact]
    public void Validate_GeneratedPdf_ReturnsWellFormedReport()
    {
        Skip.IfNot(NativeDocumentEngine.IsAvailable, "Native LM-Kit document engine is unavailable in this host.");

        var pdf = NativeDocumentEngine.PdfFromMarkdown("# Archive candidate\n\nA short document to validate for PDF/A conformance.");
        var service = Create();

        var report = service.ValidatePdfA(pdf, level: null);

        // The verdict must be one of the defined enum values.
        var validVerdicts = Enum.GetNames<PdfAValidationVerdict>();
        Assert.Contains(report.Verdict, validVerdicts);

        // Findings list is always present (possibly empty), never null.
        Assert.NotNull(report.Findings);
        Assert.True(report.PageCount >= 0);
        Assert.True(report.RulesEvaluated >= 0);
    }

    [SkippableFact]
    public void Validate_WithExplicitLevel_ReturnsWellFormedReport()
    {
        Skip.IfNot(NativeDocumentEngine.IsAvailable, "Native LM-Kit document engine is unavailable in this host.");

        var pdf = NativeDocumentEngine.PdfFromMarkdown("# Level check\n\nValidate against an explicit PDF/A-2b target.");
        var service = Create();

        var report = service.ValidatePdfA(pdf, PdfAConformanceLevel.PdfA2b);

        Assert.Contains(report.Verdict, Enum.GetNames<PdfAValidationVerdict>());
        Assert.NotNull(report.Findings);
    }
}
