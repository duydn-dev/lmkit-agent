using LMKit.Document.Pdf;
using LMKit.Document.Search;
using LmKitOmniApi.Infrastructure.AI.Documents;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LmKitOmniApi.Tests;

/// <summary>
/// Unit tests for <see cref="DocumentRedactionService"/> — the pure document-API
/// wrapper over LM-Kit.NET's PdfRedactor / OfficeRedactor. The gating and validation
/// tests are hermetic (they throw BEFORE any LM-Kit call, so no native engine is
/// touched). The real-redaction test builds a PDF with MarkdownToPdf, redacts a
/// secret token, and proves the token is gone by searching the output with PdfSearch
/// — this runs the real native engine and is only skipped if that engine genuinely
/// cannot load in the host.
/// </summary>
public sealed class DocumentRedactionServiceTests
{
    // A minimal byte array with the PDF magic bytes — passes the magic-byte guard so
    // validation-order tests can reach the term checks WITHOUT a real PDF (LM-Kit is
    // never invoked because the term guard throws first).
    private static readonly byte[] FakePdf = "%PDF-1.7 not-a-real-pdf"u8.ToArray();

    private static DocumentToolsOptions Options(Action<DocumentToolsOptions>? tweak = null)
    {
        var options = new DocumentToolsOptions
        {
            Enabled = true,
            MaxInputBytes = 25L * 1024 * 1024,
            MaxSearchTerms = 50,
            MaxOutputBytes = 25L * 1024 * 1024,
        };
        tweak?.Invoke(options);
        return options;
    }

    private static DocumentRedactionService Create(DocumentToolsOptions options) =>
        new(Microsoft.Extensions.Options.Options.Create(options), NullLogger<DocumentRedactionService>.Instance);

    // ── 1. Enable gating ──

    [Fact]
    public void Disabled_RedactPdf_Throws_WithoutTouchingLmKit()
    {
        var service = Create(Options(o => o.Enabled = false));

        Assert.False(service.IsEnabled);
        Assert.Throws<DocumentToolsDisabledException>(
            () => service.RedactPdf(FakePdf, new[] { "secret" }, caseSensitive: false, wholeWord: false));
    }

    [Fact]
    public void Disabled_ValidatePdfA_Throws()
    {
        var service = Create(Options(o => o.Enabled = false));
        Assert.Throws<DocumentToolsDisabledException>(() => service.ValidatePdfA(FakePdf, level: null));
    }

    [Fact]
    public void IsEnabled_ReflectsOptions()
    {
        Assert.True(Create(Options()).IsEnabled);
        Assert.False(Create(Options(o => o.Enabled = false)).IsEnabled);
    }

    // ── 2. Input validation (all fire BEFORE any LM-Kit call) ──

    [Fact]
    public void RedactPdf_NonPdfMagicBytes_Throws()
    {
        var service = Create(Options());
        var notPdf = new byte[] { 0x00, 0x01, 0x02, 0x03, 0x04, 0x05 };

        Assert.Throws<DocumentValidationException>(
            () => service.RedactPdf(notPdf, new[] { "secret" }, false, false));
    }

    [Fact]
    public void RedactPdf_OverInputLimit_Throws()
    {
        var service = Create(Options(o => o.MaxInputBytes = 8));
        var oversize = "%PDF-1.7 aaaaaaaaaaaaaaaaaaaa"u8.ToArray();

        Assert.Throws<DocumentValidationException>(
            () => service.RedactPdf(oversize, new[] { "secret" }, false, false));
    }

    [Fact]
    public void RedactPdf_TooManyTerms_Throws()
    {
        var service = Create(Options(o => o.MaxSearchTerms = 2));
        var terms = new[] { "a", "b", "c" };

        Assert.Throws<DocumentValidationException>(
            () => service.RedactPdf(FakePdf, terms, false, false));
    }

    [Fact]
    public void RedactPdf_EmptyTerms_Throws()
    {
        var service = Create(Options());

        Assert.Throws<DocumentValidationException>(
            () => service.RedactPdf(FakePdf, new[] { "  ", "" }, false, false));
    }

    [Fact]
    public void RedactOffice_NonZipMagicBytes_Throws()
    {
        var service = Create(Options());
        var notZip = "%PDF-1.7 actually a pdf"u8.ToArray();

        Assert.Throws<DocumentValidationException>(
            () => service.RedactOffice(notZip, ".docx", new[] { "secret" }, false, false));
    }

    [Fact]
    public void RedactOffice_UnsupportedExtension_Throws()
    {
        var service = Create(Options());
        var zip = new byte[] { 0x50, 0x4B, 0x03, 0x04, 0x14, 0x00 };

        Assert.Throws<DocumentValidationException>(
            () => service.RedactOffice(zip, ".pdf", new[] { "secret" }, false, false));
    }

    [Fact]
    public void ValidatePdfA_NonPdfMagicBytes_Throws()
    {
        var service = Create(Options());
        var notPdf = new byte[] { 0x50, 0x4B, 0x03, 0x04 }; // a zip, not a pdf

        Assert.Throws<DocumentValidationException>(() => service.ValidatePdfA(notPdf, level: null));
    }

    // ── 3. Real redaction (native engine) — the secret must be gone from the output ──

    [SkippableFact]
    public void RedactPdf_RemovesSecret_AndSearchFindsNoMatch()
    {
        Skip.IfNot(NativeDocumentEngine.IsAvailable, "Native LM-Kit document engine is unavailable in this host.");

        const string secret = "ZZTOPSECRET1234";
        var pdf = NativeDocumentEngine.PdfFromMarkdown(
            $"# Confidential Report\n\nThe secret token is {secret} and it must be removed before sharing.\n\nOrdinary sentence that should survive redaction.");

        var service = Create(Options());
        var (data, report) = service.RedactPdf(pdf, new[] { secret }, caseSensitive: false, wholeWord: false);

        Assert.True(report.ContentRemoved, "redaction should report content removed");
        Assert.True(report.SearchMatches > 0, "the secret should have been matched");
        Assert.True(report.RemovedGlyphs > 0, "glyphs should have been removed");
        Assert.NotEmpty(data);

        // Prove it: write the redacted bytes and search them — the secret must be gone.
        var tempPath = Path.Combine(Path.GetTempPath(), $"lmkit-redact-{Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllBytes(tempPath, data);
            var afterRedaction = PdfSearch.FindText(
                tempPath, secret,
                textOptions: new TextSearchOptions { Comparison = StringComparison.OrdinalIgnoreCase });

            Assert.Equal(0, afterRedaction.TotalMatches);
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }

    [SkippableFact]
    public void RedactPdf_LeavesUnrelatedTextIntact()
    {
        Skip.IfNot(NativeDocumentEngine.IsAvailable, "Native LM-Kit document engine is unavailable in this host.");

        const string secret = "REMOVEME9090";
        const string survivor = "KEEPTHISPHRASE";
        var pdf = NativeDocumentEngine.PdfFromMarkdown($"# Doc\n\n{secret} appears here.\n\n{survivor} appears here too.");

        var service = Create(Options());
        var (data, _) = service.RedactPdf(pdf, new[] { secret }, caseSensitive: false, wholeWord: false);

        var tempPath = Path.Combine(Path.GetTempPath(), $"lmkit-redact-{Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllBytes(tempPath, data);
            Assert.Equal(0, PdfSearch.FindText(tempPath, secret).TotalMatches);
            Assert.True(PdfSearch.FindText(tempPath, survivor).TotalMatches > 0, "unrelated text must survive redaction");
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }

    // ── 4. Real Office (.docx) redaction — re-extract and prove the secret is gone ──

    [SkippableFact]
    public void RedactOffice_RemovesSecretFromDocx_AndSurvivorRemains()
    {
        Skip.IfNot(NativeDocumentEngine.IsAvailable, "Native LM-Kit document engine is unavailable in this host.");

        const string secret = "ZZOFFICESECRET4242";
        const string survivor = "KEEPTHISOFFICEPHRASE";
        var docx = DocumentFixtures.MinimalDocx(
            $"The secret token is {secret} and it must be removed before sharing.",
            $"{survivor} is an ordinary sentence that should survive redaction.");

        // Precondition: the secret really is in the source document before we redact.
        Assert.Contains(secret, DocumentFixtures.ExtractDocxText(docx));

        var service = Create(Options());
        var (data, report) = service.RedactOffice(docx, ".docx", new[] { secret }, caseSensitive: false, wholeWord: false);

        Assert.True(report.ContentRemoved, "redaction should report content removed");
        Assert.True(report.ReplacedOccurrences > 0, "the secret should have been matched and replaced");
        Assert.NotEmpty(data);

        // The real proof (the service otherwise only trusts the report flag): re-open the
        // produced .docx and confirm the secret is ACTUALLY gone from the readable text
        // AND from every part of the package — while the unrelated phrase survives, which
        // also proves the output is still a valid, readable OpenXML document.
        Assert.DoesNotContain(secret, DocumentFixtures.ExtractDocxText(data));
        Assert.False(DocumentFixtures.AnyPartContains(data, secret), "the secret must not linger in any package part");
        Assert.Contains(survivor, DocumentFixtures.ExtractDocxText(data));
    }
}
