using System.Text;

namespace LmKitOmniApi.Tests;

/// <summary>Deterministic, model-free document fixtures shared by the document-tool tests.</summary>
internal static class DocumentFixtures
{
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
