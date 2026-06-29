using System;
using System.ComponentModel;
using LMKit.Agents.Tools;
using ImageMagick;

namespace LmKitOmniApi.Infrastructure.Tools.Images
{
    public partial class ImageTools
    {
        [LMFunction("Image_ExportPNG", "Exports the image as PNG.")]
        public string ExportPNG([Description("File path to save the exported PNG")] string filePath)
        {
            if (!CheckIfOpen()) return "No image is open.";
            _image!.Format = MagickFormat.Png;
            _image.Write(filePath);
            return $"Image exported as PNG to {filePath}.";
        }

        [LMFunction("Image_ExportJPEG", "Exports the image as JPEG.")]
        public string ExportJPEG(
            [Description("File path to save the exported JPEG")] string filePath,
            [Description("Quality of JPEG (0-100, default 85)")] int quality = 85)
        {
            if (!CheckIfOpen()) return "No image is open.";
            _image!.Format = MagickFormat.Jpeg;
            _image.Quality = (uint)quality;
            _image.Write(filePath);
            return $"Image exported as JPEG to {filePath} with quality {quality}.";
        }

        [LMFunction("Image_ExportBMP", "Auto-generated stub for ExportBMP")]
        public string ExportBMP()
        {
            throw new NotImplementedException("ExportBMP is not fully implemented yet.");
        }

        [LMFunction("Image_ExportGIF", "Auto-generated stub for ExportGIF")]
        public string ExportGIF()
        {
            throw new NotImplementedException("ExportGIF is not fully implemented yet.");
        }

        [LMFunction("Image_ExportTIFF", "Auto-generated stub for ExportTIFF")]
        public string ExportTIFF()
        {
            throw new NotImplementedException("ExportTIFF is not fully implemented yet.");
        }

        [LMFunction("Image_ExportWEBP", "Exports the image as WEBP.")]
        public string ExportWEBP(
            [Description("File path to save the exported WEBP")] string filePath,
            [Description("Quality of WEBP (0-100, default 80)")] int quality = 80)
        {
            if (!CheckIfOpen()) return "No image is open.";
            _image!.Format = MagickFormat.WebP;
            _image.Quality = (uint)quality;
            _image.Write(filePath);
            return $"Image exported as WEBP to {filePath} with quality {quality}.";
        }

        [LMFunction("Image_ExportSVG", "Auto-generated stub for ExportSVG")]
        public string ExportSVG()
        {
            throw new NotImplementedException("ExportSVG is not fully implemented yet.");
        }

        [LMFunction("Image_ExportPDF", "Auto-generated stub for ExportPDF")]
        public string ExportPDF()
        {
            throw new NotImplementedException("ExportPDF is not fully implemented yet.");
        }

        [LMFunction("Image_ExportPSD", "Auto-generated stub for ExportPSD")]
        public string ExportPSD()
        {
            throw new NotImplementedException("ExportPSD is not fully implemented yet.");
        }

        [LMFunction("Image_ExportICO", "Auto-generated stub for ExportICO")]
        public string ExportICO()
        {
            throw new NotImplementedException("ExportICO is not fully implemented yet.");
        }

        [LMFunction("Image_GenerateThumbnail", "Generates a thumbnail of the image and saves it to a new file.")]
        public string GenerateThumbnail(
            [Description("File path to save the thumbnail")] string filePath,
            [Description("Width of the thumbnail")] int width,
            [Description("Height of the thumbnail")] int height)
        {
            if (!CheckIfOpen()) return "No image is open.";
            
            // Create a clone to avoid modifying the original image
            using (var thumbnail = _image!.Clone())
            {
                thumbnail.Resize((uint)width, (uint)height);
                thumbnail.Write(filePath);
            }
            return $"Thumbnail generated and saved to {filePath} with size {width}x{height}.";
        }

        [LMFunction("Image_CreateSpriteSheet", "Auto-generated stub for CreateSpriteSheet")]
        public string CreateSpriteSheet()
        {
            throw new NotImplementedException("CreateSpriteSheet is not fully implemented yet.");
        }

        [LMFunction("Image_SplitSpriteSheet", "Auto-generated stub for SplitSpriteSheet")]
        public string SplitSpriteSheet()
        {
            throw new NotImplementedException("SplitSpriteSheet is not fully implemented yet.");
        }

        [LMFunction("Image_OptimizeForWeb", "Auto-generated stub for OptimizeForWeb")]
        public string OptimizeForWeb()
        {
            throw new NotImplementedException("OptimizeForWeb is not fully implemented yet.");
        }

        [LMFunction("Image_GeneratePreview", "Auto-generated stub for GeneratePreview")]
        public string GeneratePreview()
        {
            throw new NotImplementedException("GeneratePreview is not fully implemented yet.");
        }
    }
}
