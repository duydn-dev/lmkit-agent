using System;
using LMKit.Agents.Tools;
using System.ComponentModel;
using Aspose.Words;
using Aspose.Words.Drawing;

namespace LmKitOmniApi.Infrastructure.Tools.Office.Word
{
    public partial class WordTools
    {
        [LMFunction("Word_InsertImage", "Inserts an image from a local file path into the document.")]
        public string InsertImage([Description("The absolute path of the image file")] string imagePath)
        {
            if (!_isOpen || _document == null) return "No document is open.";
            try
            {
                var builder = new DocumentBuilder(_document);
                builder.MoveToDocumentEnd();
                builder.InsertImage(imagePath);
                return "Image inserted successfully.";
            }
            catch (Exception ex)
            {
                return $"Failed to insert image: {ex.Message}";
            }
        }

        [LMFunction("Word_ResizeImage", "Resizes an image at a specific shape index.")]
        public string ResizeImage(
            [Description("The 0-based index of the image/shape in the document")] int imageIndex,
            [Description("New width in points")] double width,
            [Description("New height in points")] double height)
        {
            if (!_isOpen || _document == null) return "No document is open.";
            var shapes = _document.GetChildNodes(NodeType.Shape, true);
            if (imageIndex >= 0 && imageIndex < shapes.Count)
            {
                var shape = (Shape)shapes[imageIndex];
                if (shape.HasImage)
                {
                    shape.Width = width;
                    shape.Height = height;
                    return "Image resized successfully.";
                }
                return "The specified shape is not an image.";
            }
            return "Image index out of range.";
        }
        
        [LMFunction("Word_SetImageAlignment", "Sets the horizontal alignment of an image.")]
        public string SetImageAlignment(
            [Description("The 0-based index of the image")] int imageIndex,
            [Description("Alignment: 'Left', 'Center', 'Right'")] string alignment)
        {
            if (!_isOpen || _document == null) return "No document is open.";
            var shapes = _document.GetChildNodes(NodeType.Shape, true);
            if (imageIndex >= 0 && imageIndex < shapes.Count)
            {
                var shape = (Shape)shapes[imageIndex];
                if (Enum.TryParse(alignment, true, out HorizontalAlignment align))
                {
                    shape.HorizontalAlignment = align;
                    return $"Image alignment set to {align}.";
                }
                return "Invalid alignment value.";
            }
            return "Image index out of range.";
        }
    }
}
