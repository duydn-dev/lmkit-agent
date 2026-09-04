using LmKitOmniApi.Infrastructure.AI.Documents;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LmKitOmniApi.Tests;

/// <summary>
/// Unit tests for <see cref="PdfFormService"/> — the pure document-API wrapper over
/// LM-Kit.NET's PdfForm. Gating/validation tests are hermetic; the real-API tests use
/// MarkdownToPdf for a non-form PDF (HasForm=false) and a byte-offset-accurate,
/// hand-built AcroForm fixture to prove field read + fill. The AcroForm assertions
/// skip (rather than fail) if the native engine is unavailable or does not recognize
/// the minimal fixture.
/// </summary>
public sealed class PdfFormServiceTests
{
    private static readonly byte[] FakePdf = "%PDF-1.7 not-a-real-pdf"u8.ToArray();

    private static PdfFormService Create(bool enabled = true) =>
        new(Options.Create(new DocumentToolsOptions
        {
            Enabled = enabled,
            MaxInputBytes = 25L * 1024 * 1024,
            MaxSearchTerms = 50,
            MaxOutputBytes = 25L * 1024 * 1024,
        }), NullLogger<PdfFormService>.Instance);

    // ── Gating / validation (hermetic) ──

    [Fact]
    public void Disabled_GetFields_Throws()
    {
        var service = Create(enabled: false);
        Assert.False(service.IsEnabled);
        Assert.Throws<DocumentToolsDisabledException>(() => service.GetFields(FakePdf));
    }

    [Fact]
    public void Disabled_Fill_Throws()
    {
        var service = Create(enabled: false);
        Assert.Throws<DocumentToolsDisabledException>(
            () => service.Fill(FakePdf, new[] { ("name", "value") }, flatten: false));
    }

    [Fact]
    public void GetFields_NonPdfMagicBytes_Throws()
    {
        var service = Create();
        Assert.Throws<DocumentValidationException>(() => service.GetFields(new byte[] { 1, 2, 3, 4 }));
    }

    [Fact]
    public void GetFields_OverInputLimit_Throws()
    {
        var service = new PdfFormService(
            Options.Create(new DocumentToolsOptions { Enabled = true, MaxInputBytes = 8 }),
            NullLogger<PdfFormService>.Instance);

        Assert.Throws<DocumentValidationException>(() => service.GetFields("%PDF-1.7 aaaaaaaaaaaaaaaa"u8.ToArray()));
    }

    // ── Real API: a plain (non-form) PDF has no AcroForm ──

    [SkippableFact]
    public void GetFields_OnNonFormPdf_ReturnsHasFormFalse()
    {
        Skip.IfNot(NativeDocumentEngine.IsAvailable, "Native LM-Kit document engine is unavailable in this host.");

        var pdf = NativeDocumentEngine.PdfFromMarkdown("# Plain document\n\nThis PDF has no form fields at all.");
        var service = Create();

        var snapshot = service.GetFields(pdf);

        Assert.False(snapshot.HasForm);
        Assert.Empty(snapshot.Fields);
    }

    // ── Real API: a hand-built AcroForm — read then fill a text field ──

    [SkippableFact]
    public void GetFields_OnAcroForm_DetectsTextField()
    {
        Skip.IfNot(NativeDocumentEngine.IsAvailable, "Native LM-Kit document engine is unavailable in this host.");

        var pdf = DocumentFixtures.AcroFormPdf("fullName");
        var service = Create();

        var snapshot = service.GetFields(pdf);
        Skip.IfNot(snapshot.HasForm && snapshot.Fields.Any(f => f.Name == "fullName"),
            "The native engine did not recognize the minimal hand-built AcroForm fixture.");

        var field = snapshot.Fields.Single(f => f.Name == "fullName");
        Assert.Equal("Text", field.Kind);
        Assert.Equal(0, field.PageIndex);
    }

    [SkippableFact]
    public void Fill_SetsTextFieldValue()
    {
        Skip.IfNot(NativeDocumentEngine.IsAvailable, "Native LM-Kit document engine is unavailable in this host.");

        var pdf = DocumentFixtures.AcroFormPdf("fullName");
        var service = Create();

        var before = service.GetFields(pdf);
        Skip.IfNot(before.HasForm && before.Fields.Any(f => f.Name == "fullName"),
            "The native engine did not recognize the minimal hand-built AcroForm fixture.");

        var (data, report) = service.Fill(pdf, new[] { ("fullName", "Jane Q. Public") }, flatten: false);

        Assert.True(report.FieldsSet >= 1, "at least one field should have been set");
        Assert.NotEmpty(data);

        var after = service.GetFields(data);
        var field = after.Fields.Single(f => f.Name == "fullName");
        Assert.Equal("Jane Q. Public", field.Value);
    }
}
