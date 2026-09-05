using System.IO.Compression;
using System.Security;
using System.Text;
using System.Xml.Linq;

namespace LmKitOmniApi.Tests;

/// <summary>Deterministic, model-free document fixtures shared by the document-tool tests.</summary>
internal static class DocumentFixtures
{
    // The WordprocessingML main namespace — the one <w:t> text runs live in.
    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    /// <summary>
    /// Builds a minimal but valid <c>.docx</c> (OpenXML WordprocessingML) package from
    /// one <c>&lt;w:t&gt;</c> run per supplied paragraph. Pure byte assembly via
    /// <see cref="ZipArchive"/> — no native engine and no OpenXML SDK — and the first
    /// bytes are the ZIP <c>PK\x03\x04</c> magic the Office validator sniffs for.
    /// Each paragraph is a single contiguous text run so a redactor that removes a term
    /// makes that term genuinely disappear from the part (nothing to reassemble).
    /// </summary>
    public static byte[] MinimalDocx(params string[] paragraphs)
    {
        const string contentTypes =
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
            "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
            "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
            "<Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/>" +
            "</Types>";

        const string rootRels =
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/>" +
            "</Relationships>";

        var body = new StringBuilder();
        foreach (var paragraph in paragraphs)
            body.Append("<w:p><w:r><w:t xml:space=\"preserve\">")
                .Append(SecurityElement.Escape(paragraph))
                .Append("</w:t></w:r></w:p>");

        var documentXml =
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\">" +
            "<w:body>" + body + "<w:sectPr/></w:body></w:document>";

        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(zip, "[Content_Types].xml", contentTypes);
            WriteEntry(zip, "_rels/.rels", rootRels);
            WriteEntry(zip, "word/document.xml", documentXml);
        }
        return buffer.ToArray();

        static void WriteEntry(ZipArchive zip, string path, string content)
        {
            using var stream = zip.CreateEntry(path, CompressionLevel.Optimal).Open();
            var bytes = Encoding.UTF8.GetBytes(content);
            stream.Write(bytes, 0, bytes.Length);
        }
    }

    /// <summary>
    /// Extracts the visible text of a <c>.docx</c> by concatenating every
    /// <c>&lt;w:t&gt;</c> run in <c>word/document.xml</c> — the readable-text view a
    /// reader would see, used to prove a redacted term is truly gone.
    /// </summary>
    public static string ExtractDocxText(byte[] docx)
    {
        using var buffer = new MemoryStream(docx);
        using var zip = new ZipArchive(buffer, ZipArchiveMode.Read);
        var entry = zip.GetEntry("word/document.xml")
            ?? throw new InvalidOperationException("document.xml missing from the .docx package.");
        using var stream = entry.Open();
        var document = XDocument.Load(stream);
        return string.Concat(document.Descendants(W + "t").Select(t => t.Value));
    }

    /// <summary>
    /// True when <paramref name="term"/> appears in the decoded bytes of ANY part of the
    /// package — the strongest "it is really gone" check, catching a term left behind in
    /// any part, not just the main document body.
    /// </summary>
    public static bool AnyPartContains(byte[] docx, string term)
    {
        using var buffer = new MemoryStream(docx);
        using var zip = new ZipArchive(buffer, ZipArchiveMode.Read);
        foreach (var entry in zip.Entries)
        {
            using var stream = entry.Open();
            using var reader = new StreamReader(stream, Encoding.UTF8);
            if (reader.ReadToEnd().Contains(term, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Builds a minimal, byte-offset-accurate single-text-field AcroForm PDF. All
    /// content is ASCII so char offsets equal byte offsets; the xref table therefore
    /// points at the real object offsets. Pure byte assembly — no native engine.
    /// </summary>
    public static byte[] AcroFormPdf(string fieldName)
    {
        string[] bodies =
        {
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [4 0 R] /NeedAppearances true /DA (/Helv 0 Tf 0 g) /DR << /Font << /Helv 5 0 R >> >> >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Annots [4 0 R] /Resources << /Font << /Helv 5 0 R >> >> >>",
            $"<< /Type /Annot /Subtype /Widget /FT /Tx /T ({fieldName}) /Rect [100 700 300 720] /V () /DA (/Helv 12 Tf 0 g) /F 4 /P 3 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Name /Helv >>",
        };

        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");

        var offsets = new int[bodies.Length + 1];
        for (var i = 0; i < bodies.Length; i++)
        {
            offsets[i + 1] = sb.Length;
            sb.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }

        var xrefOffset = sb.Length;
        sb.Append("xref\n");
        sb.Append("0 ").Append(bodies.Length + 1).Append('\n');
        sb.Append("0000000000 65535 f\r\n");
        for (var i = 1; i <= bodies.Length; i++)
            sb.Append(offsets[i].ToString("D10")).Append(" 00000 n\r\n");

        sb.Append("trailer\n<< /Size ").Append(bodies.Length + 1).Append(" /Root 1 0 R >>\n");
        sb.Append("startxref\n").Append(xrefOffset).Append("\n%%EOF");

        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
