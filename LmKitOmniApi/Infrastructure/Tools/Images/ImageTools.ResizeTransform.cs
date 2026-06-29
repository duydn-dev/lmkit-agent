using System;
using System.ComponentModel;
using LMKit.Agents.Tools;
using ImageMagick;

namespace LmKitOmniApi.Infrastructure.Tools.Images
{
    public partial class ImageTools
    {
        [LMFunction("Image_ResizeImage", "Resizes the image to the specified width and height.")]
        public string ResizeImage(
            [Description("New width of the image")] int width, 
            [Description("New height of the image")] int height)
        {
            if (!CheckIfOpen()) return "No image is open.";
            _image!.Resize((uint)width, (uint)height);
            return $"Image resized to {width}x{height}.";
        }

        [LMFunction("Image_ScaleImage", "Scales the image by a percentage.")]
        public string ScaleImage([Description("Percentage to scale (e.g. 50 for half size, 200 for double size)")] double percentage)
        {
            if (!CheckIfOpen()) return "No image is open.";
            _image!.Scale(new Percentage(percentage));
            return $"Image scaled by {percentage}%.";
        }

        [LMFunction("Image_CropImage", "Crops the image to the specified geometry.")]
        public string CropImage(
            [Description("X coordinate of the top-left corner")] int x,
            [Description("Y coordinate of the top-left corner")] int y,
            [Description("Width of the cropped area")] int width,
            [Description("Height of the cropped area")] int height)
        {
            if (!CheckIfOpen()) return "No image is open.";
            _image!.Crop(new MagickGeometry(x, y, (uint)width, (uint)height));
            return $"Image cropped at ({x},{y}) with size {width}x{height}.";
        }

        [LMFunction("Image_RotateImage", "Rotates the image by the specified degrees.")]
        public string RotateImage([Description("Degrees to rotate (e.g. 90, 180)")] double degrees)
        {
            if (!CheckIfOpen()) return "No image is open.";
            _image!.Rotate(degrees);
            return $"Image rotated by {degrees} degrees.";
        }

        [LMFunction("Image_FlipHorizontal", "Flips the image horizontally (left to right).")]
        public string FlipHorizontal()
        {
            if (!CheckIfOpen()) return "No image is open.";
            _image!.Flop();
            return "Image flipped horizontally.";
        }

        [LMFunction("Image_FlipVertical", "Flips the image vertically (top to bottom).")]
        public string FlipVertical()
        {
            if (!CheckIfOpen()) return "No image is open.";
            _image!.Flip();
            return "Image flipped vertically.";
        }

        [LMFunction("Image_TrimTransparentBorders", "Trims the edges of the image that are the same color as the background/transparent.")]
        public string TrimTransparentBorders()
        {
            if (!CheckIfOpen()) return "No image is open.";
            _image!.Trim();
            return "Image borders trimmed.";
        }

        [LMFunction("Image_DeskewImage", "Removes skew from the image (often used for scanned text).")]
        public string DeskewImage([Description("Threshold for deskewing (e.g. 40)")] double threshold)
        {
            if (!CheckIfOpen()) return "No image is open.";
            _image!.Deskew(new Percentage(threshold));
            return $"Image deskewed with threshold {threshold}%.";
        }

        [LMFunction("Image_SkewImage", "Auto-generated stub for SkewImage")]
        public string SkewImage()
        {
            throw new NotImplementedException("SkewImage is not fully implemented yet.");
        }

        [LMFunction("Image_PerspectiveTransform", "Auto-generated stub for PerspectiveTransform")]
        public string PerspectiveTransform()
        {
            throw new NotImplementedException("PerspectiveTransform is not fully implemented yet.");
        }

        [LMFunction("Image_ShearImage", "Auto-generated stub for ShearImage")]
        public string ShearImage()
        {
            throw new NotImplementedException("ShearImage is not fully implemented yet.");
        }

        [LMFunction("Image_PadImage", "Auto-generated stub for PadImage")]
        public string PadImage()
        {
            throw new NotImplementedException("PadImage is not fully implemented yet.");
        }

        [LMFunction("Image_AutoCrop", "Auto-generated stub for AutoCrop")]
        public string AutoCrop()
        {
            throw new NotImplementedException("AutoCrop is not fully implemented yet.");
        }

        [LMFunction("Image_AutoRotate", "Auto-generated stub for AutoRotate")]
        public string AutoRotate()
        {
            throw new NotImplementedException("AutoRotate is not fully implemented yet.");
        }

        [LMFunction("Image_StraightenImage", "Auto-generated stub for StraightenImage")]
        public string StraightenImage()
        {
            throw new NotImplementedException("StraightenImage is not fully implemented yet.");
        }
    }
}
