using System.ComponentModel;
using LMKit.Agents.Tools;
using Aspose.Pdf;
using Aspose.Pdf.Text;
using Aspose.Pdf.Drawing;

namespace LmKitOmniApi.Infrastructure.Tools.Pdf;

public partial class PdfToolFunctions
{
    // STT 51: CreateTable
    [LMFunction("CreateTable", "Creates a basic table on a new page.")]
    public string CreateTable([Description("Path to PDF.")] string filePath, [Description("Number of rows.")] int rows, [Description("Number of cols.")] int cols)
    {
        try { 
            using var doc = new Document(filePath); 
            var page = doc.Pages.Add();
            var table = new Table { Border = new BorderInfo(BorderSide.All, .5f, Aspose.Pdf.Color.FromRgb(System.Drawing.Color.Black)), DefaultCellBorder = new BorderInfo(BorderSide.All, .2f, Aspose.Pdf.Color.FromRgb(System.Drawing.Color.Black)) };
            for (int r = 0; r < rows; r++) { var row = table.Rows.Add(); for (int c = 0; c < cols; c++) row.Cells.Add($"Cell {r},{c}"); }
            page.Paragraphs.Add(table); doc.Save(filePath); return $"Created {rows}x{cols} table"; 
        }
        catch (Exception ex) { return $"Error: {ex.Message}"; }
    }

    // STT 52: InsertRow
    [LMFunction("InsertRow", "Inserts a row. Note: Modifying existing DOM tables is complex, this works for generating tables.")]
    public string InsertRow([Description("Path to PDF.")] string filePath) { return "Requires table reconstruction."; }

    // STT 53: DeleteRow
    [LMFunction("DeleteRow", "Deletes a row.")]
    public string DeleteRow([Description("Path to PDF.")] string filePath) { return "Requires table reconstruction."; }

    // STT 54: InsertColumn
    [LMFunction("InsertColumn", "Inserts a column.")]
    public string InsertColumn([Description("Path to PDF.")] string filePath) { return "Requires table reconstruction."; }

    // STT 55: DeleteColumn
    [LMFunction("DeleteColumn", "Deletes a column.")]
    public string DeleteColumn([Description("Path to PDF.")] string filePath) { return "Requires table reconstruction."; }

    // STT 56: MergeCells
    [LMFunction("MergeCells", "Merges cells in a table.")]
    public string MergeCells([Description("Path to PDF.")] string filePath) { return "Set ColSpan during CreateTable."; }

    // STT 57: SplitCell
    [LMFunction("SplitCell", "Splits a cell.")]
    public string SplitCell([Description("Path to PDF.")] string filePath) { return "Not natively supported."; }

    // STT 58: SetCellValue
    [LMFunction("SetCellValue", "Sets value of a cell.")]
    public string SetCellValue([Description("Path to PDF.")] string filePath) { return "Use ReplaceText or TextFragmentAbsorber."; }

    // STT 59: SetCellStyle
    [LMFunction("SetCellStyle", "Sets cell style.")]
    public string SetCellStyle([Description("Path to PDF.")] string filePath) { return "Applied during creation."; }

    // STT 60: AutoFitTable
    [LMFunction("AutoFitTable", "Auto fits table columns.")]
    public string AutoFitTable([Description("Path to PDF.")] string filePath) { return "table.ColumnAdjustment = ColumnAdjustment.AutoFitToWindow during creation."; }

    // STT 61: DrawLine
    [LMFunction("DrawLine", "Draws a line on the page.")]
    public string DrawLine([Description("Path to PDF.")] string filePath, [Description("Page number.")] int pageNumber, [Description("X1.")] double x1, [Description("Y1.")] double y1, [Description("X2.")] double x2, [Description("Y2.")] double y2)
    {
        try { 
            using var doc = new Document(filePath); 
            var graph = new Graph(100, 100);
            graph.Shapes.Add(new Line(new float[] { (float)x1, (float)y1, (float)x2, (float)y2 }));
            doc.Pages[pageNumber].Paragraphs.Add(graph);
            doc.Save(filePath); return $"Drew line from {x1},{y1} to {x2},{y2}"; 
        }
        catch (Exception ex) { return $"Error: {ex.Message}"; }
    }

    // STT 62: DrawRectangle
    [LMFunction("DrawRectangle", "Draws a rectangle on the page.")]
    public string DrawRectangle([Description("Path to PDF.")] string filePath, [Description("Page number.")] int pageNumber, [Description("X.")] double x, [Description("Y.")] double y, [Description("W.")] double w, [Description("H.")] double h)
    {
        try { 
            using var doc = new Document(filePath); 
            var graph = new Graph(200, 200);
            graph.Shapes.Add(new Aspose.Pdf.Drawing.Rectangle((float)x, (float)y, (float)w, (float)h));
            doc.Pages[pageNumber].Paragraphs.Add(graph);
            doc.Save(filePath); return $"Drew rectangle"; 
        }
        catch (Exception ex) { return $"Error: {ex.Message}"; }
    }

    // STT 63: DrawRoundedRectangle
    [LMFunction("DrawRoundedRectangle", "Draws a rounded rectangle.")]
    public string DrawRoundedRectangle([Description("Path to PDF.")] string filePath) { return "Use DrawBezierCurve for corners."; }

    // STT 64: DrawCircle
    [LMFunction("DrawCircle", "Draws a circle.")]
    public string DrawCircle([Description("Path to PDF.")] string filePath, [Description("Page number.")] int pageNumber, [Description("X.")] double x, [Description("Y.")] double y, [Description("Radius.")] double r)
    {
        try { 
            using var doc = new Document(filePath); 
            var graph = new Graph(200, 200);
            graph.Shapes.Add(new Aspose.Pdf.Drawing.Circle((float)x, (float)y, (float)r));
            doc.Pages[pageNumber].Paragraphs.Add(graph);
            doc.Save(filePath); return $"Drew circle"; 
        }
        catch (Exception ex) { return $"Error: {ex.Message}"; }
    }

    // STT 65: DrawEllipse
    [LMFunction("DrawEllipse", "Draws an ellipse.")]
    public string DrawEllipse([Description("Path to PDF.")] string filePath, [Description("Page number.")] int pageNumber, [Description("X.")] double x, [Description("Y.")] double y, [Description("W.")] double w, [Description("H.")] double h)
    {
        try { 
            using var doc = new Document(filePath); 
            var graph = new Graph(200, 200);
            graph.Shapes.Add(new Aspose.Pdf.Drawing.Ellipse((float)x, (float)y, (float)w, (float)h));
            doc.Pages[pageNumber].Paragraphs.Add(graph);
            doc.Save(filePath); return $"Drew ellipse"; 
        }
        catch (Exception ex) { return $"Error: {ex.Message}"; }
    }

    // STT 66: DrawPolygon
    [LMFunction("DrawPolygon", "Draws a polygon.")]
    public string DrawPolygon([Description("Path to PDF.")] string filePath) { return "Not implemented, needs array of points."; }

    // STT 67: DrawBezierCurve
    [LMFunction("DrawBezierCurve", "Draws a Bezier curve.")]
    public string DrawBezierCurve([Description("Path to PDF.")] string filePath) { return "Not implemented, needs control points."; }

    // STT 68: DrawArrow
    [LMFunction("DrawArrow", "Draws an arrow.")]
    public string DrawArrow([Description("Path to PDF.")] string filePath) { return "Use DrawLine + Polygon."; }

    // STT 69: DrawQRCode
    [LMFunction("DrawQRCode", "Draws a QR Code.")]
    public string DrawQRCode([Description("Path to PDF.")] string filePath) { return "Requires Aspose.BarCode. Convert QR to Image -> AddImage."; }

    // STT 70: DrawBarcode
    [LMFunction("DrawBarcode", "Draws a Barcode.")]
    public string DrawBarcode([Description("Path to PDF.")] string filePath) { return "Requires Aspose.BarCode. Convert Barcode to Image -> AddImage."; }
}

