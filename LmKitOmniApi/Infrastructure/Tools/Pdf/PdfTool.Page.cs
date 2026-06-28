using System.ComponentModel;
using LMKit.Agents.Tools;
using Aspose.Pdf;

namespace LmKitOmniApi.Infrastructure.Tools.Pdf;

public partial class PdfToolFunctions
{
    // STT 11: GetPageCount
    [LMFunction("GetPageCount", "Gets the total number of pages in a PDF document.")]
    public string GetPageCount([Description("The path to the PDF file.")] string filePath)
    {
        try { using var doc = new Document(filePath); return $"Total pages: {doc.Pages.Count}"; }
        catch (Exception ex) { return $"Error: {ex.Message}"; }
    }

    // STT 12: GetPageInfo
    [LMFunction("GetPageInfo", "Gets information about a specific page (dimensions, rotation).")]
    public string GetPageInfo([Description("Path to PDF.")] string filePath, [Description("Page number (1-based).")] int pageNumber)
    {
        try { 
            using var doc = new Document(filePath); 
            var page = doc.Pages[pageNumber];
            return $"Page {pageNumber} -> Width: {page.Rect.Width}, Height: {page.Rect.Height}, Rotation: {page.Rotate}"; 
        }
        catch (Exception ex) { return $"Error: {ex.Message}"; }
    }

    // STT 13: AddPage
    [LMFunction("AddPage", "Adds a new empty page to the end of the PDF document.")]
    public string AddPage([Description("Path to PDF.")] string filePath)
    {
        try { using var doc = new Document(filePath); doc.Pages.Add(); doc.Save(filePath); return $"Added a new page to {filePath}"; }
        catch (Exception ex) { return $"Error: {ex.Message}"; }
    }

    // STT 14: InsertPage
    [LMFunction("InsertPage", "Inserts an empty page at the specified position.")]
    public string InsertPage([Description("Path to PDF.")] string filePath, [Description("Index to insert the page (1-based).")] int index)
    {
        try { using var doc = new Document(filePath); doc.Pages.Insert(index); doc.Save(filePath); return $"Inserted page at index {index}"; }
        catch (Exception ex) { return $"Error: {ex.Message}"; }
    }

    // STT 15: DeletePage
    [LMFunction("DeletePage", "Deletes a specific page from the PDF document.")]
    public string DeletePage([Description("Path to PDF.")] string filePath, [Description("Page number to delete (1-based).")] int pageNumber)
    {
        try { using var doc = new Document(filePath); doc.Pages.Delete(pageNumber); doc.Save(filePath); return $"Deleted page {pageNumber}"; }
        catch (Exception ex) { return $"Error: {ex.Message}"; }
    }

    // STT 16: MovePage
    [LMFunction("MovePage", "Moves a page from one position to another.")]
    public string MovePage([Description("Path to PDF.")] string filePath, [Description("Source page index.")] int sourceIndex, [Description("Target page index.")] int targetIndex)
    {
        try { 
            using var doc = new Document(filePath); 
            var page = doc.Pages[sourceIndex];
            doc.Pages.Insert(targetIndex, page);
            // If moving forward, the source index is now shifted
            doc.Pages.Delete(sourceIndex > targetIndex ? sourceIndex + 1 : sourceIndex);
            doc.Save(filePath); 
            return $"Moved page {sourceIndex} to {targetIndex}"; 
        }
        catch (Exception ex) { return $"Error: {ex.Message}"; }
    }

    // STT 17: RotatePage
    [LMFunction("RotatePage", "Rotates a specific page (90, 180, 270 degrees).")]
    public string RotatePage([Description("Path to PDF.")] string filePath, [Description("Page number.")] int pageNumber, [Description("Rotation enum value (0=None, 1=on90, 2=on180, 3=on270).")] int rotation)
    {
        try { 
            using var doc = new Document(filePath); 
            doc.Pages[pageNumber].Rotate = (Rotation)rotation; 
            doc.Save(filePath); 
            return $"Rotated page {pageNumber}"; 
        }
        catch (Exception ex) { return $"Error: {ex.Message}"; }
    }

    // STT 18: ResizePage
    [LMFunction("ResizePage", "Resizes a specific page to new dimensions.")]
    public string ResizePage([Description("Path to PDF.")] string filePath, [Description("Page number.")] int pageNumber, [Description("New width.")] double width, [Description("New height.")] double height)
    {
        try { 
            using var doc = new Document(filePath); 
            doc.Pages[pageNumber].SetPageSize(width, height); 
            doc.Save(filePath); 
            return $"Resized page {pageNumber} to {width}x{height}"; 
        }
        catch (Exception ex) { return $"Error: {ex.Message}"; }
    }

    // STT 19: ExtractPages
    [LMFunction("ExtractPages", "Extracts specific pages from a PDF to a new document.")]
    public string ExtractPages([Description("Source PDF path.")] string sourcePath, [Description("Output PDF path.")] string destPath, [Description("Start page (1-based).")] int startPage, [Description("End page (1-based).")] int endPage)
    {
        try { 
            using var sourceDoc = new Document(sourcePath); 
            using var newDoc = new Document();
            for (int i = startPage; i <= endPage; i++) newDoc.Pages.Add(sourceDoc.Pages[i]);
            newDoc.Save(destPath); 
            return $"Extracted pages {startPage}-{endPage} to {destPath}"; 
        }
        catch (Exception ex) { return $"Error: {ex.Message}"; }
    }

    // STT 20: MergeDocuments
    [LMFunction("MergeDocuments", "Merges two PDF documents into one.")]
    public string MergeDocuments([Description("First PDF path.")] string path1, [Description("Second PDF path.")] string path2, [Description("Output PDF path.")] string output)
    {
        try { 
            using var doc1 = new Document(path1); 
            using var doc2 = new Document(path2); 
            doc1.Pages.Add(doc2.Pages);
            doc1.Save(output); 
            return $"Merged documents into {output}"; 
        }
        catch (Exception ex) { return $"Error: {ex.Message}"; }
    }
}

