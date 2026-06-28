using System.ComponentModel;
using LMKit.Agents.Tools;
using Aspose.Pdf;
using Aspose.Pdf.Text;
using Aspose.Pdf.Annotations;

namespace LmKitOmniApi.Infrastructure.Tools.Pdf;

public partial class PdfToolFunctions
{
    // STT 21: AddText
    [LMFunction("AddText", "Adds a simple text block to a specific page.")]
    public string AddText([Description("Path to PDF.")] string filePath, [Description("Page number.")] int pageNumber, [Description("Text to add.")] string text)
    {
        try { 
            using var doc = new Document(filePath); 
            var textFragment = new TextFragment(text);
            var textBuilder = new TextBuilder(doc.Pages[pageNumber]);
            textBuilder.AppendText(textFragment);
            doc.Save(filePath); 
            return $"Added text to page {pageNumber}"; 
        }
        catch (Exception ex) { return $"Error: {ex.Message}"; }
    }

    // STT 22: InsertText
    [LMFunction("InsertText", "Inserts text at specific X, Y coordinates.")]
    public string InsertText([Description("Path to PDF.")] string filePath, [Description("Page number.")] int pageNumber, [Description("Text string.")] string text, [Description("X coordinate.")] double x, [Description("Y coordinate.")] double y)
    {
        try { 
            using var doc = new Document(filePath); 
            var textFragment = new TextFragment(text);
            textFragment.Position = new Position(x, y);
            var textBuilder = new TextBuilder(doc.Pages[pageNumber]);
            textBuilder.AppendText(textFragment);
            doc.Save(filePath); 
            return $"Inserted text at {x},{y}"; 
        }
        catch (Exception ex) { return $"Error: {ex.Message}"; }
    }

    // STT 23: ReplaceText
    [LMFunction("ReplaceText", "Finds and replaces text across the entire PDF document.")]
    public string ReplaceText([Description("Path to PDF.")] string filePath, [Description("Old text.")] string oldText, [Description("New text.")] string newText)
    {
        try { 
            using var doc = new Document(filePath); 
            var absorber = new TextFragmentAbsorber(oldText);
            doc.Pages.Accept(absorber);
            foreach (TextFragment fragment in absorber.TextFragments) fragment.Text = newText;
            doc.Save(filePath); 
            return $"Replaced '{oldText}' with '{newText}'"; 
        }
        catch (Exception ex) { return $"Error: {ex.Message}"; }
    }

    // STT 24: FindText
    [LMFunction("FindText", "Searches for a specific text string and returns the number of occurrences.")]
    public string FindText([Description("Path to PDF.")] string filePath, [Description("Text to find.")] string searchText)
    {
        try { 
            using var doc = new Document(filePath); 
            var absorber = new TextFragmentAbsorber(searchText);
            doc.Pages.Accept(absorber);
            return $"Found {absorber.TextFragments.Count} occurrences of '{searchText}'"; 
        }
        catch (Exception ex) { return $"Error: {ex.Message}"; }
    }

    // STT 25: SearchTextRegex
    [LMFunction("SearchTextRegex", "Searches for text using a Regular Expression.")]
    public string SearchTextRegex([Description("Path to PDF.")] string filePath, [Description("Regex pattern.")] string regexPattern)
    {
        try { 
            using var doc = new Document(filePath); 
            var absorber = new TextFragmentAbsorber(regexPattern, new TextSearchOptions(true));
            doc.Pages.Accept(absorber);
            return $"Found {absorber.TextFragments.Count} matches for regex '{regexPattern}'"; 
        }
        catch (Exception ex) { return $"Error: {ex.Message}"; }
    }

    // STT 26: ExtractText
    [LMFunction("ExtractText", "Extracts all plain text from a specific page or the whole document.")]
    public string ExtractText([Description("Path to PDF.")] string filePath, [Description("Page number (0 for all pages).")] int pageNumber)
    {
        try { 
            using var doc = new Document(filePath); 
            var absorber = new TextAbsorber();
            if (pageNumber > 0) doc.Pages[pageNumber].Accept(absorber);
            else doc.Pages.Accept(absorber);
            return absorber.Text; 
        }
        catch (Exception ex) { return $"Error: {ex.Message}"; }
    }

    // STT 27: HighlightText
    [LMFunction("HighlightText", "Highlights occurrences of a specific text string.")]
    public string HighlightText([Description("Path to PDF.")] string filePath, [Description("Text to highlight.")] string searchText)
    {
        try { 
            using var doc = new Document(filePath); 
            var absorber = new TextFragmentAbsorber(searchText);
            doc.Pages.Accept(absorber);
            foreach (TextFragment frag in absorber.TextFragments)
            {
                var highlight = new HighlightAnnotation(frag.Page, frag.Rectangle);
                frag.Page.Annotations.Add(highlight);
            }
            doc.Save(filePath); 
            return $"Highlighted {absorber.TextFragments.Count} occurrences of '{searchText}'"; 
        }
        catch (Exception ex) { return $"Error: {ex.Message}"; }
    }

    // STT 28: UnderlineText
    [LMFunction("UnderlineText", "Adds an underline annotation to occurrences of a specific text string.")]
    public string UnderlineText([Description("Path to PDF.")] string filePath, [Description("Text to underline.")] string searchText)
    {
        try { 
            using var doc = new Document(filePath); 
            var absorber = new TextFragmentAbsorber(searchText);
            doc.Pages.Accept(absorber);
            foreach (TextFragment frag in absorber.TextFragments)
            {
                var underline = new UnderlineAnnotation(frag.Page, frag.Rectangle);
                frag.Page.Annotations.Add(underline);
            }
            doc.Save(filePath); 
            return $"Underlined {absorber.TextFragments.Count} occurrences of '{searchText}'"; 
        }
        catch (Exception ex) { return $"Error: {ex.Message}"; }
    }

    // STT 29: StrikeoutText
    [LMFunction("StrikeoutText", "Adds a strikeout annotation to occurrences of a specific text string.")]
    public string StrikeoutText([Description("Path to PDF.")] string filePath, [Description("Text to strikeout.")] string searchText)
    {
        try { 
            using var doc = new Document(filePath); 
            var absorber = new TextFragmentAbsorber(searchText);
            doc.Pages.Accept(absorber);
            foreach (TextFragment frag in absorber.TextFragments)
            {
                var strikeout = new StrikeOutAnnotation(frag.Page, frag.Rectangle);
                frag.Page.Annotations.Add(strikeout);
            }
            doc.Save(filePath); 
            return $"Striked out {absorber.TextFragments.Count} occurrences of '{searchText}'"; 
        }
        catch (Exception ex) { return $"Error: {ex.Message}"; }
    }

    // STT 30: RedactText
    [LMFunction("RedactText", "Redacts (blacks out) specific text strings from the document.")]
    public string RedactText([Description("Path to PDF.")] string filePath, [Description("Text to redact.")] string searchText)
    {
        try { 
            using var doc = new Document(filePath); 
            var absorber = new TextFragmentAbsorber(searchText);
            doc.Pages.Accept(absorber);
            foreach (TextFragment frag in absorber.TextFragments)
            {
                var redact = new RedactionAnnotation(frag.Page, frag.Rectangle);
                frag.Page.Annotations.Add(redact);
                redact.Redact(); // Applies the redaction permanently
            }
            doc.Save(filePath); 
            return $"Redacted {absorber.TextFragments.Count} occurrences of '{searchText}'"; 
        }
        catch (Exception ex) { return $"Error: {ex.Message}"; }
    }
}

