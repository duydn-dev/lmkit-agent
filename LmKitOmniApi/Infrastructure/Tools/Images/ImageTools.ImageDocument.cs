using System;
using System.ComponentModel;
using System.IO;
using LMKit.Agents.Tools;
using ImageMagick;

namespace LmKitOmniApi.Infrastructure.Tools.Images
{
    public partial class ImageTools
    {
        [LMFunction("Image_CreateImage", "Creates a new blank image.")]
        public string CreateImage(
            [Description("Width of the image")] int width, 
            [Description("Height of the image")] int height, 
            [Description("Background color (e.g. 'White', 'Transparent', '#FF0000')")] string color)
        {
            try
            {
                var magickColor = new MagickColor(color);
                _image = new MagickImage(magickColor, (uint)width, (uint)height);
                _isOpen = true;
                _currentFilePath = null;
                return $"Created blank image {width}x{height} with color {color}.";
            }
            catch (Exception ex)
            {
                return $"Error creating image: {ex.Message}";
            }
        }

        [LMFunction("Image_OpenImage", "Opens an image file.")]
        public string OpenImage([Description("Absolute path to the image file")] string filePath)
        {
            if (!File.Exists(filePath)) return $"File not found: {filePath}";
            try
            {
                Dispose();
                _image = new MagickImage(filePath);
                _isOpen = true;
                _currentFilePath = filePath;
                return $"Image opened successfully: {filePath}";
            }
            catch (Exception ex)
            {
                return $"Error opening image: {ex.Message}";
            }
        }

        [LMFunction("Image_SaveImage", "Saves the current image.")]
        public string SaveImage()
        {
            if (!CheckIfOpen()) return "No image is currently open.";
            if (string.IsNullOrEmpty(_currentFilePath)) return "No file path associated. Use SaveImageAs.";
            try
            {
                _image!.Write(_currentFilePath);
                return $"Image saved to {_currentFilePath}";
            }
            catch (Exception ex)
            {
                return $"Error saving image: {ex.Message}";
            }
        }

        [LMFunction("Image_SaveImageAs", "Saves the current image to a new path.")]
        public string SaveImageAs([Description("Absolute path to save the image")] string filePath)
        {
            if (!CheckIfOpen()) return "No image is currently open.";
            try
            {
                _image!.Write(filePath);
                _currentFilePath = filePath;
                return $"Image saved to {filePath}";
            }
            catch (Exception ex)
            {
                return $"Error saving image: {ex.Message}";
            }
        }

        [LMFunction("Image_CloseImage", "Closes the current image.")]
        public string CloseImage()
        {
            if (!CheckIfOpen()) return "No image is currently open.";
            Dispose();
            return "Image closed.";
        }

        [LMFunction("Image_CloneImage", "Clones the current image.")]
        public string CloneImage()
        {
            if (!CheckIfOpen()) return "No image is open.";
            var oldPath = _currentFilePath;
            _image = new MagickImage(_image!);
            _currentFilePath = oldPath != null ? oldPath + "_clone" : null;
            return "Image cloned.";
        }

        [LMFunction("Image_ResizeCanvas", "Resizes the canvas without resizing the image.")]
        public string ResizeCanvas(
            [Description("New width of the canvas")] int width, 
            [Description("New height of the canvas")] int height)
        {
            if (!CheckIfOpen()) return "No image is open.";
            _image!.Extent((uint)width, (uint)height);
            return $"Canvas resized to {width}x{height}.";
        }

        [LMFunction("Image_GetImageInfo", "Gets information about the current image.")]
        public string GetImageInfo()
        {
            if (!CheckIfOpen()) return "No image is currently open.";
            return $"Width: {_image!.Width}, Height: {_image.Height}, Format: {_image.Format}, ColorSpace: {_image.ColorSpace}";
        }

        [LMFunction("Image_ConvertImageFormat", "Converts the image format (e.g. from PNG to JPEG).")]
        public string ConvertImageFormat([Description("New format (e.g. 'Jpeg', 'Png', 'WebP')")] string format)
        {
            if (!CheckIfOpen()) return "No image is open.";
            if (Enum.TryParse<MagickFormat>(format, true, out var magickFormat))
            {
                _image!.Format = magickFormat;
                return $"Image format converted to {format}.";
            }
            return $"Invalid format: {format}";
        }

        [LMFunction("Image_CropCanvas", "Auto-generated stub for CropCanvas")]
        public string CropCanvas()
        {
            throw new NotImplementedException("CropCanvas is not fully implemented yet.");
        }

        [LMFunction("Image_RotateCanvas", "Auto-generated stub for RotateCanvas")]
        public string RotateCanvas()
        {
            throw new NotImplementedException("RotateCanvas is not fully implemented yet.");
        }

        [LMFunction("Image_FlipCanvas", "Auto-generated stub for FlipCanvas")]
        public string FlipCanvas()
        {
            throw new NotImplementedException("FlipCanvas is not fully implemented yet.");
        }

        [LMFunction("Image_SetImageMetadata", "Auto-generated stub for SetImageMetadata")]
        public string SetImageMetadata()
        {
            throw new NotImplementedException("SetImageMetadata is not fully implemented yet.");
        }

        [LMFunction("Image_OptimizeImage", "Auto-generated stub for OptimizeImage")]
        public string OptimizeImage()
        {
            throw new NotImplementedException("OptimizeImage is not fully implemented yet.");
        }

        [LMFunction("Image_CompressImage", "Auto-generated stub for CompressImage")]
        public string CompressImage()
        {
            throw new NotImplementedException("CompressImage is not fully implemented yet.");
        }
    }
}
