using System.ComponentModel;
using LMKit.Agents.Tools;
using Aspose.Pdf;

namespace LmKitOmniApi.Infrastructure.Tools.Pdf;

public partial class PdfToolFunctions
{
    // STT 1: CreateDocument
    [LMFunction("CreateDocument", "Creates a new, empty PDF document and saves it to the specified path.")]
    public string CreateDocument([Description("The path where the new PDF will be saved.")] string outputPath)
    {
        try { using var doc = new Document(); doc.Save(outputPath); return $"Created successfully at {outputPath}"; }
        catch (Exception ex) { return $"Error: {ex.Message}"; }
    }

    // STT 2: OpenDocument (Validates if a PDF can be opened)
    [LMFunction("OpenDocument", "Opens and validates a PDF document.")]
    public string OpenDocument([Description("The path to the PDF file.")] string filePath)
    {
        try { using var doc = new Document(filePath); return $"Successfully opened {filePath}. Pages: {doc.Pages.Count}"; }
        catch (Exception ex) { return $"Error: {ex.Message}"; }
    }

    // STT 3: SaveDocument (Used after memory operations, here just resaves it for consistency)
    [LMFunction("SaveDocument", "Saves a PDF document (useful for forcing an overwrite).")]
    public string SaveDocument([Description("The path to the PDF file.")] string filePath)
    {
        try { using var doc = new Document(filePath); doc.Save(filePath); return $"Saved {filePath}"; }
        catch (Exception ex) { return $"Error: {ex.Message}"; }
    }

    // STT 4: SaveAs
    [LMFunction("SaveAs", "Saves an existing PDF document to a new destination path.")]
    public string SaveAs([Description("Source PDF path.")] string sourcePath, [Description("Destination PDF path.")] string destinationPath)
    {
        try { using var doc = new Document(sourcePath); doc.Save(destinationPath); return $"Saved as {destinationPath}"; }
        catch (Exception ex) { return $"Error: {ex.Message}"; }
    }

    // STT 5: CloseDocument
    [LMFunction("CloseDocument", "Closes a PDF document and frees memory. (In .NET this handles garbage collection implicitly).")]
    public string CloseDocument([Description("The path to the PDF file.")] string filePath)
    {
        return $"Document {filePath} marked as closed.";
    }

    // STT 6: GetDocumentInfo
    [LMFunction("GetDocumentInfo", "Retrieves metadata information (Author, Title, Subject) from a PDF document.")]
    public string GetDocumentInfo([Description("The path to the PDF file.")] string filePath)
    {
        try { 
            using var doc = new Document(filePath); 
            return $"Author: {doc.Info.Author}, Title: {doc.Info.Title}, Subject: {doc.Info.Subject}, Creator: {doc.Info.Creator}"; 
        }
        catch (Exception ex) { return $"Error: {ex.Message}"; }
    }

    // STT 7: SetDocumentInfo
    [LMFunction("SetDocumentInfo", "Updates metadata information in a PDF document.")]
    public string SetDocumentInfo([Description("Path to PDF.")] string filePath, [Description("Author name.")] string author, [Description("Document title.")] string title)
    {
        try { 
            using var doc = new Document(filePath); 
            doc.Info.Author = author; 
            doc.Info.Title = title; 
            doc.Save(filePath); 
            return $"Metadata updated for {filePath}"; 
        }
        catch (Exception ex) { return $"Error: {ex.Message}"; }
    }

    // STT 8: CloneDocument
    [LMFunction("CloneDocument", "Clones a PDF document by copying it to a new location.")]
    public string CloneDocument([Description("Source PDF path.")] string sourcePath, [Description("Cloned PDF path.")] string destinationPath)
    {
        try { System.IO.File.Copy(sourcePath, destinationPath, true); return $"Document cloned to {destinationPath}"; }
        catch (Exception ex) { return $"Error: {ex.Message}"; }
    }

    // STT 9: OptimizeDocument
    [LMFunction("OptimizeDocument", "Optimizes a PDF document size by removing unused objects and compressing images.")]
    public string OptimizeDocument([Description("Path to PDF.")] string filePath)
    {
        try { 
            using var doc = new Document(filePath); 
            doc.OptimizeResources(); 
            doc.Save(filePath); 
            return $"Optimized {filePath}"; 
        }
        catch (Exception ex) { return $"Error: {ex.Message}"; }
    }

    // STT 10: ValidateDocument
    [LMFunction("ValidateDocument", "Validates a PDF document for PDF/A compliance.")]
    public string ValidateDocument([Description("Path to PDF.")] string filePath)
    {
        try { 
            using var doc = new Document(filePath); 
            bool isValid = doc.Validate("validation_log.xml", PdfFormat.PDF_A_1B); 
            return isValid ? "Document is valid PDF/A-1B." : "Validation failed. Check validation_log.xml."; 
        }
        catch (Exception ex) { return $"Error: {ex.Message}"; }
    }
}

