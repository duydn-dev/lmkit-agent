using LmKitOmniApi.Application.Documents;

namespace LmKitOmniApi.Tests;

/// <summary>
/// The shared file-name → doc-type mapping. The worker, the ad-hoc ingest path and
/// the query-filter extractor all route through this, so a file stored as "pdf" is
/// also what a "file pdf" query filters on.
/// </summary>
public sealed class DocumentTypeClassifierTests
{
    [Theory]
    [InlineData("bao-cao.pdf", "pdf")]
    [InlineData("KE-HOACH.PDF", "pdf")]
    [InlineData("hop-dong.docx", "word")]
    [InlineData("legacy.doc", "word")]
    [InlineData("so-lieu.xlsx", "excel")]
    [InlineData("data.csv", "excel")]
    [InlineData("slide.pptx", "powerpoint")]
    [InlineData("anh.png", "image")]
    [InlineData("scan.JPEG", "image")]
    [InlineData("ghi-chu.txt", "text")]
    [InlineData("readme.md", "markdown")]
    [InlineData("archive.zip", "other")]
    [InlineData("no-extension", "other")]
    public void FromFileName_MapsExtensionToDocType(string fileName, string expected)
    {
        Assert.Equal(expected, DocumentTypeClassifier.FromFileName(fileName));
    }

    [Fact]
    public void FromFileName_NullOrEmpty_IsOther()
    {
        Assert.Equal("other", DocumentTypeClassifier.FromFileName(null));
        Assert.Equal("other", DocumentTypeClassifier.FromFileName(""));
    }
}
