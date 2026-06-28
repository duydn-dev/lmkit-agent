using System.ComponentModel;
using LMKit.Agents.Tools;
using Aspose.Pdf;
using Aspose.Pdf.Text;

namespace LmKitOmniApi.Infrastructure.Tools.Pdf;

public partial class PdfToolFunctions
{
    // STT 31: SetFont
    [LMFunction("SetFont", "Changes the font of a specific text string.")]
    public string SetFont([Description("Path to PDF.")] string filePath, [Description("Text to change.")] string text, [Description("Font name (e.g. Arial).")] string fontName)
    {
        try { 
            using var doc = new Document(filePath); 
            var absorber = new TextFragmentAbsorber(text); doc.Pages.Accept(absorber);
            foreach (TextFragment frag in absorber.TextFragments) frag.TextState.Font = FontRepository.FindFont(fontName);
            doc.Save(filePath); return $"Changed font to {fontName} for '{text}'"; 
        }
        catch (Exception ex) { return $"Error: {ex.Message}"; }
    }

    // STT 32: SetFontSize
    [LMFunction("SetFontSize", "Changes the font size of a specific text string.")]
    public string SetFontSize([Description("Path to PDF.")] string filePath, [Description("Text to change.")] string text, [Description("Font size.")] float size)
    {
        try { 
            using var doc = new Document(filePath); 
            var absorber = new TextFragmentAbsorber(text); doc.Pages.Accept(absorber);
            foreach (TextFragment frag in absorber.TextFragments) frag.TextState.FontSize = size;
            doc.Save(filePath); return $"Changed font size to {size} for '{text}'"; 
        }
        catch (Exception ex) { return $"Error: {ex.Message}"; }
    }

    // STT 33: SetFontColor
    [LMFunction("SetFontColor", "Changes the text color. Colors: Red, Green, Blue, Black, etc.")]
    public string SetFontColor([Description("Path to PDF.")] string filePath, [Description("Text to change.")] string text, [Description("Color name (e.g. Red).")] string colorName)
    {
        try { 
            using var doc = new Document(filePath); 
            var absorber = new TextFragmentAbsorber(text); doc.Pages.Accept(absorber);
            var color = System.Drawing.Color.FromName(colorName);
            foreach (TextFragment frag in absorber.TextFragments) frag.TextState.ForegroundColor = Aspose.Pdf.Color.FromRgb(color);
            doc.Save(filePath); return $"Changed font color to {colorName} for '{text}'"; 
        }
        catch (Exception ex) { return $"Error: {ex.Message}"; }
    }

    // STT 34: SetTextAlignment
    [LMFunction("SetTextAlignment", "Sets text alignment for a text fragment.")]
    public string SetTextAlignment([Description("Path to PDF.")] string filePath, [Description("Text to align.")] string text, [Description("0=Left, 1=Center, 2=Right, 3=Justify")] int alignment)
    {
        try { 
            using var doc = new Document(filePath); 
            var absorber = new TextFragmentAbsorber(text); doc.Pages.Accept(absorber);
            foreach (TextFragment frag in absorber.TextFragments) frag.TextState.HorizontalAlignment = (HorizontalAlignment)alignment;
            doc.Save(filePath); return $"Aligned text '{text}'"; 
        }
        catch (Exception ex) { return $"Error: {ex.Message}"; }
    }

    // STT 35: SetLineSpacing
    [LMFunction("SetLineSpacing", "Sets line spacing for text.")]
    public string SetLineSpacing([Description("Path to PDF.")] string filePath, [Description("Text.")] string text, [Description("Spacing value.")] float spacing)
    {
        try { 
            using var doc = new Document(filePath); 
            var absorber = new TextFragmentAbsorber(text); doc.Pages.Accept(absorber);
            foreach (TextFragment frag in absorber.TextFragments) frag.TextState.LineSpacing = spacing;
            doc.Save(filePath); return $"Set line spacing to {spacing}"; 
        }
        catch (Exception ex) { return $"Error: {ex.Message}"; }
    }

    // STT 36: SetParagraphSpacing
    [LMFunction("SetParagraphSpacing", "Simulates paragraph spacing using margins.")]
    public string SetParagraphSpacing([Description("Path to PDF.")] string filePath, [Description("Spacing value.")] float spacing)
    {
        return "Not fully supported for existing paragraphs natively without layout reconstruction. Requires DOM recreation.";
    }

    // STT 37: SetTextRotation
    [LMFunction("SetTextRotation", "Rotates specific text.")]
    public string SetTextRotation([Description("Path to PDF.")] string filePath, [Description("Text.")] string text, [Description("Angle in degrees.")] double angle)
    {
        try { 
            using var doc = new Document(filePath); 
            var absorber = new TextFragmentAbsorber(text); doc.Pages.Accept(absorber);
            foreach (TextFragment frag in absorber.TextFragments) frag.TextState.Rotation = angle;
            doc.Save(filePath); return $"Rotated text by {angle} degrees"; 
        }
        catch (Exception ex) { return $"Error: {ex.Message}"; }
    }

    // STT 38: SetTextOpacity
    [LMFunction("SetTextOpacity", "Sets opacity for text.")]
    public string SetTextOpacity([Description("Path to PDF.")] string filePath, [Description("Text.")] string text, [Description("Opacity (0 to 255).")] int alpha)
    {
        return "Aspose 18.3 TextState doesn't support alpha channel directly on Color without advanced graphic state manipulation.";
    }

    // STT 39: ListFonts
    [LMFunction("ListFonts", "Lists all embedded fonts in the document.")]
    public string ListFonts([Description("Path to PDF.")] string filePath)
    {
        try { 
            using var doc = new Document(filePath); 
            var fontNames = new List<string>();
            foreach (Page page in doc.Pages) {
                if (page.Resources.Fonts != null) {
                    foreach (Aspose.Pdf.Text.Font font in page.Resources.Fonts) {
                        fontNames.Add(font.FontName);
                    }
                }
            }
            return $"Fonts: {string.Join(", ", fontNames.Distinct())}"; 
        }
        catch (Exception ex) { return $"Error: {ex.Message}"; }
    }

    // STT 40: EmbedFonts
    [LMFunction("EmbedFonts", "Embeds standard fonts into the PDF to ensure it looks the same everywhere.")]
    public string EmbedFonts([Description("Path to PDF.")] string filePath)
    {
        try { 
            using var doc = new Document(filePath); 
            foreach (Page page in doc.Pages) {
                if (page.Resources.Fonts != null) {
                    foreach (Aspose.Pdf.Text.Font font in page.Resources.Fonts) {
                        font.IsEmbedded = true;
                    }
                }
            }
            doc.Save(filePath); return $"Embedded all used fonts into {filePath}"; 
        }
        catch (Exception ex) { return $"Error: {ex.Message}"; }
    }

    // STT 41: AddImage
    [LMFunction("AddImage", "Adds an image to a specific page.")]
    public string AddImage([Description("Path to PDF.")] string filePath, [Description("Page number.")] int pageNumber, [Description("Path to Image.")] string imagePath)
    {
        try { 
            using var doc = new Document(filePath); 
            doc.Pages[pageNumber].AddImage(imagePath, new Rectangle(100, 100, 300, 300));
            doc.Save(filePath); return $"Added image to page {pageNumber}"; 
        }
        catch (Exception ex) { return $"Error: {ex.Message}"; }
    }

    // STT 42: ReplaceImage
    [LMFunction("ReplaceImage", "Replaces an existing image with a new one.")]
    public string ReplaceImage([Description("Path to PDF.")] string filePath, [Description("Page number.")] int pageNumber, [Description("Image index (1-based).")] int imageIndex, [Description("Path to New Image.")] string imagePath)
    {
        try { 
            using var doc = new Document(filePath); 
            using var fs = new FileStream(imagePath, FileMode.Open);
            doc.Pages[pageNumber].Resources.Images.Replace(imageIndex, fs);
            doc.Save(filePath); return $"Replaced image {imageIndex} on page {pageNumber}"; 
        }
        catch (Exception ex) { return $"Error: {ex.Message}"; }
    }

    // STT 43: DeleteImage
    [LMFunction("DeleteImage", "Deletes an image from a page.")]
    public string DeleteImage([Description("Path to PDF.")] string filePath, [Description("Page number.")] int pageNumber, [Description("Image index (1-based).")] int imageIndex)
    {
        try { 
            using var doc = new Document(filePath); 
            doc.Pages[pageNumber].Resources.Images.Delete(imageIndex);
            doc.Save(filePath); return $"Deleted image {imageIndex} from page {pageNumber}"; 
        }
        catch (Exception ex) { return $"Error: {ex.Message}"; }
    }

    // STT 44: ExtractImages
    [LMFunction("ExtractImages", "Extracts images from a page and saves them to disk.")]
    public string ExtractImages([Description("Path to PDF.")] string filePath, [Description("Page number.")] int pageNumber, [Description("Output directory path.")] string outputDir)
    {
        try { 
            using var doc = new Document(filePath); 
            int count = 0;
            foreach (XImage image in doc.Pages[pageNumber].Resources.Images)
            {
                using var fs = new FileStream(Path.Combine(outputDir, $"image_{pageNumber}_{++count}.jpg"), FileMode.Create);
                image.Save(fs, System.Drawing.Imaging.ImageFormat.Jpeg);
            }
            return $"Extracted {count} images from page {pageNumber}"; 
        }
        catch (Exception ex) { return $"Error: {ex.Message}"; }
    }

    // STT 45: ResizeImage
    [LMFunction("ResizeImage", "Resizes an image. (Note: Typically image dimensions are fixed by the PDF Matrix, this extracts and replaces).")]
    public string ResizeImage([Description("Path to PDF.")] string filePath)
    {
        return "Image resizing directly within PDF resources requires complex XObject matrix transformation. Suggest external resize then ReplaceImage.";
    }

    // STT 46: RotateImage
    [LMFunction("RotateImage", "Rotates an image.")]
    public string RotateImage([Description("Path to PDF.")] string filePath)
    {
         return "Image rotation within PDF resources requires modifying the page's contents stream matrix. Not fully supported by basic tool.";
    }

    // STT 47: CropImage
    [LMFunction("CropImage", "Crops an image.")]
    public string CropImage([Description("Path to PDF.")] string filePath)
    {
        return "Use ExtractImage -> Crop locally using System.Drawing -> ReplaceImage.";
    }

    // STT 48: CompressImages
    [LMFunction("CompressImages", "Compresses all images in the document to reduce file size.")]
    public string CompressImages([Description("Path to PDF.")] string filePath, [Description("Quality (1-100).")] int quality)
    {
        return "Aspose.PDF 18.3 does not natively support ImageCompressionOptions. Please use external tools to compress images before embedding.";
    }

    // STT 49: SetImageOpacity
    [LMFunction("SetImageOpacity", "Sets image opacity.")]
    public string SetImageOpacity([Description("Path to PDF.")] string filePath)
    {
        return "Image opacity requires Advanced Graphics State (ExtGState). Not implemented in basic tool.";
    }

    // STT 50: ConvertImageFormat
    [LMFunction("ConvertImageFormat", "Converts the first page of PDF to an image.")]
    public string ConvertImageFormat([Description("Path to PDF.")] string filePath, [Description("Output Image Path.")] string outputImagePath)
    {
        try { 
            using var doc = new Document(filePath); 
            var resolution = new Aspose.Pdf.Devices.Resolution(300);
            var jpegDevice = new Aspose.Pdf.Devices.JpegDevice(resolution);
            jpegDevice.Process(doc.Pages[1], outputImagePath);
            return $"Converted page 1 to {outputImagePath}"; 
        }
        catch (Exception ex) { return $"Error: {ex.Message}"; }
    }
}

