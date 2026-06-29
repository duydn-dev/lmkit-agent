using System.ComponentModel;
using LMKit.Agents.Tools;
using Aspose.Pdf;
using Aspose.Pdf.Annotations;
using Aspose.Pdf.Forms;
using Aspose.Pdf.Facades;

namespace LmKitOmniApi.Infrastructure.Tools.Pdf;

public partial class PdfToolFunctions
{
    // STT 71: AddHighlightAnnotation
    [LMFunction("AddHighlightAnnotation", "Adds highlight annotation. (Already covered in HighlightText).")]
    public string AddHighlightAnnotation([Description("Path to PDF.")] string filePath) { return "Use HighlightText function."; }

    // STT 72: AddFreeTextAnnotation
    [LMFunction("AddFreeTextAnnotation", "Adds a free text annotation.")]
    public string AddFreeTextAnnotation([Description("Path to PDF.")] string filePath, [Description("Page.")] int page, [Description("Text.")] string text)
    {
        try { 
            using var doc = new Document(filePath); 
            var fta = new FreeTextAnnotation(doc.Pages[page], new Aspose.Pdf.Rectangle(100, 100, 200, 200), new DefaultAppearance());
            fta.Contents = text;
            doc.Pages[page].Annotations.Add(fta); doc.Save(filePath); return $"Added free text annotation"; 
        }
        catch (Exception ex) { return $"Error: {ex.Message}"; }
    }

    // STT 76: GetFormFields
    [LMFunction("GetFormFields", "Gets all form fields.")]
    public string GetFormFields([Description("Path to PDF.")] string filePath)
    {
        try { 
            using var doc = new Document(filePath); 
            var fields = doc.Form.Fields.Select(f => f.FullName).ToList();
            return $"Fields: {string.Join(", ", fields)}"; 
        }
        catch (Exception ex) { return $"Error: {ex.Message}"; }
    }

    // STT 77: SetFormFieldValue
    [LMFunction("SetFormFieldValue", "Fills a form field.")]
    public string SetFormFieldValue([Description("Path to PDF.")] string filePath, [Description("Field name.")] string fieldName, [Description("Value.")] string value)
    {
        try { 
            using var doc = new Document(filePath); 
            if (doc.Form[fieldName] is TextBoxField tb) { tb.Value = value; doc.Save(filePath); return $"Filled {fieldName}"; }
            return "Field not found or not a text box."; 
        }
        catch (Exception ex) { return $"Error: {ex.Message}"; }
    }

    // STT 79: FlattenForm
    [LMFunction("FlattenForm", "Flattens the form making it uneditable.")]
    public string FlattenForm([Description("Path to PDF.")] string filePath)
    {
        try { 
            using var doc = new Document(filePath); 
            bool flattened = false;
            foreach (var field in doc.Form.Fields) { field.Flatten(); flattened = true; }
            if (flattened) { doc.Save(filePath); return "Form flattened."; }
            return "No form fields found.";
        }
        catch (Exception ex) { return $"Error: {ex.Message}"; }
    }

    // STT 87: AddTextWatermark
    [LMFunction("AddTextWatermark", "Adds a text watermark.")]
    public string AddTextWatermark([Description("Path to PDF.")] string filePath, [Description("Text.")] string text)
    {
        try { 
            using var doc = new Document(filePath);
            var fileStamp = new PdfFileStamp(doc);
            var stamp = new Aspose.Pdf.Facades.Stamp();
            stamp.BindLogo(new FormattedText(text, System.Drawing.Color.Gray, System.Drawing.Color.Transparent, Aspose.Pdf.Facades.FontStyle.Helvetica, EncodingType.Winansi, true, 48));
            stamp.Rotation = 45;
            stamp.Opacity = 0.5f;
            fileStamp.AddStamp(stamp);
            doc.Save(filePath + "_watermarked.pdf"); fileStamp.Close();
            return "Watermark added."; 
        }
        catch (Exception ex) { return $"Error: {ex.Message}"; }
    }

    // STT 91: CreateBookmark
    [LMFunction("CreateBookmark", "Creates a bookmark.")]
    public string CreateBookmark([Description("Path to PDF.")] string filePath, [Description("Title.")] string title, [Description("Page.")] int page)
    {
        try { 
            using var doc = new Document(filePath); 
            var outline = new OutlineItemCollection(doc.Outlines);
            outline.Title = title;
            outline.Action = new Aspose.Pdf.Annotations.GoToAction(doc.Pages[page]);
            doc.Outlines.Add(outline); doc.Save(filePath); return $"Created bookmark '{title}'"; 
        }
        catch (Exception ex) { return $"Error: {ex.Message}"; }
    }

    // STT 92: EncryptDocument
    [LMFunction("EncryptDocument", "Encrypts PDF with password.")]
    public string EncryptDocument([Description("Path to PDF.")] string filePath, [Description("Password.")] string password)
    {
        try { 
            using var doc = new Document(filePath); 
            doc.Encrypt(password, password, 0, CryptoAlgorithm.AESx128);
            doc.Save(filePath); return "Document encrypted."; 
        }
        catch (Exception ex) { return $"Error: {ex.Message}"; }
    }

    // STT 100: ConvertToWord
    [LMFunction("ConvertToWord", "Converts PDF to Word (DocX).")]
    public string ConvertToWord([Description("Path to PDF.")] string filePath, [Description("Output path.")] string outputPath)
    {
        try { 
            using var doc = new Document(filePath); 
            doc.Save(outputPath, SaveFormat.DocX); return $"Converted to Word: {outputPath}"; 
        }
        catch (Exception ex) { return $"Error: {ex.Message}"; }
    }

    // STT 103: ConvertToExcel
    [LMFunction("ConvertToExcel", "Converts PDF to Excel (XLSX).")]
    public string ConvertToExcel([Description("Path to PDF.")] string filePath, [Description("Output path.")] string outputPath)
    {
        try { 
            using var doc = new Document(filePath); 
            doc.Save(outputPath, SaveFormat.Excel); return $"Converted to Excel: {outputPath}"; 
        }
        catch (Exception ex) { return $"Error: {ex.Message}"; }
    }

    // STT 105: ConvertToPDFA
    [LMFunction("ConvertToPDFA", "Converts PDF to PDF/A.")]
    public string ConvertToPDFA([Description("Path to PDF.")] string filePath, [Description("Output path.")] string outputPath)
    {
        try { 
            using var doc = new Document(filePath); 
            doc.Convert("log.xml", PdfFormat.PDF_A_1B, ConvertErrorAction.Delete);
            doc.Save(outputPath); return $"Converted to PDF/A: {outputPath}"; 
        }
        catch (Exception ex) { return $"Error: {ex.Message}"; }
    }
}

