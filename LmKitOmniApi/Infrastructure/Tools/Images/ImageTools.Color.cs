using System;
using System.ComponentModel;
using LMKit.Agents.Tools;
using ImageMagick;

namespace LmKitOmniApi.Infrastructure.Tools.Images
{
    public partial class ImageTools
    {
        [LMFunction("Image_AdjustBrightness", "Adjusts the brightness of the image.")]
        public string AdjustBrightness([Description("Percentage (e.g. 110 to increase, 90 to decrease)")] double percentage)
        {
            if (!CheckIfOpen()) return "No image is open.";
            _image!.Modulate(new Percentage(percentage), new Percentage(100), new Percentage(100));
            return $"Brightness adjusted by {percentage}%.";
        }

        [LMFunction("Image_AdjustContrast", "Adjusts the contrast of the image.")]
        public string AdjustContrast([Description("True to increase contrast, False to decrease")] bool increase)
        {
            if (!CheckIfOpen()) return "No image is open.";
            if (increase) _image!.Contrast();
            else _image!.Level(new Percentage(10), new Percentage(90)); // Approximation
            return $"Contrast {(increase ? "increased" : "decreased")}.";
        }

        [LMFunction("Image_AdjustGamma", "Auto-generated stub for AdjustGamma")]
        public string AdjustGamma()
        {
            throw new NotImplementedException("AdjustGamma is not fully implemented yet.");
        }

        [LMFunction("Image_AdjustExposure", "Auto-generated stub for AdjustExposure")]
        public string AdjustExposure()
        {
            throw new NotImplementedException("AdjustExposure is not fully implemented yet.");
        }

        [LMFunction("Image_AdjustSaturation", "Adjusts the saturation of the image.")]
        public string AdjustSaturation([Description("Percentage (e.g. 150 to increase, 50 to decrease)")] double percentage)
        {
            if (!CheckIfOpen()) return "No image is open.";
            _image!.Modulate(new Percentage(100), new Percentage(percentage), new Percentage(100));
            return $"Saturation adjusted by {percentage}%.";
        }

        [LMFunction("Image_AdjustHue", "Adjusts the hue of the image.")]
        public string AdjustHue([Description("Percentage (100 is no change)")] double percentage)
        {
            if (!CheckIfOpen()) return "No image is open.";
            _image!.Modulate(new Percentage(100), new Percentage(100), new Percentage(percentage));
            return $"Hue adjusted by {percentage}%.";
        }

        [LMFunction("Image_AdjustTemperature", "Auto-generated stub for AdjustTemperature")]
        public string AdjustTemperature()
        {
            throw new NotImplementedException("AdjustTemperature is not fully implemented yet.");
        }

        [LMFunction("Image_AdjustTint", "Auto-generated stub for AdjustTint")]
        public string AdjustTint()
        {
            throw new NotImplementedException("AdjustTint is not fully implemented yet.");
        }

        [LMFunction("Image_AdjustVibrance", "Auto-generated stub for AdjustVibrance")]
        public string AdjustVibrance()
        {
            throw new NotImplementedException("AdjustVibrance is not fully implemented yet.");
        }

        [LMFunction("Image_ConvertToGrayscale", "Converts the image to grayscale.")]
        public string ConvertToGrayscale()
        {
            if (!CheckIfOpen()) return "No image is open.";
            _image!.Grayscale();
            return "Image converted to grayscale.";
        }

        [LMFunction("Image_ConvertToBlackWhite", "Converts the image to black and white (threshold).")]
        public string ConvertToBlackWhite()
        {
            if (!CheckIfOpen()) return "No image is open.";
            _image!.Threshold(new Percentage(50));
            return "Image converted to black and white.";
        }

        [LMFunction("Image_InvertColors", "Inverts the colors of the image (negative).")]
        public string InvertColors()
        {
            if (!CheckIfOpen()) return "No image is open.";
            _image!.Negate();
            return "Image colors inverted.";
        }

        [LMFunction("Image_SepiaEffect", "Applies a sepia tone effect to the image.")]
        public string SepiaEffect([Description("Threshold percentage (default is 80)")] double threshold)
        {
            if (!CheckIfOpen()) return "No image is open.";
            _image!.SepiaTone(new Percentage(threshold));
            return "Sepia effect applied.";
        }

        [LMFunction("Image_PosterizeImage", "Auto-generated stub for PosterizeImage")]
        public string PosterizeImage()
        {
            throw new NotImplementedException("PosterizeImage is not fully implemented yet.");
        }

        [LMFunction("Image_ThresholdImage", "Auto-generated stub for ThresholdImage")]
        public string ThresholdImage()
        {
            throw new NotImplementedException("ThresholdImage is not fully implemented yet.");
        }

        [LMFunction("Image_EqualizeHistogram", "Auto-generated stub for EqualizeHistogram")]
        public string EqualizeHistogram()
        {
            throw new NotImplementedException("EqualizeHistogram is not fully implemented yet.");
        }

        [LMFunction("Image_AutoLevels", "Auto-generated stub for AutoLevels")]
        public string AutoLevels()
        {
            throw new NotImplementedException("AutoLevels is not fully implemented yet.");
        }

        [LMFunction("Image_AutoContrast", "Auto-generated stub for AutoContrast")]
        public string AutoContrast()
        {
            throw new NotImplementedException("AutoContrast is not fully implemented yet.");
        }

        [LMFunction("Image_ReplaceColor", "Auto-generated stub for ReplaceColor")]
        public string ReplaceColor()
        {
            throw new NotImplementedException("ReplaceColor is not fully implemented yet.");
        }

        [LMFunction("Image_MakeTransparent", "Auto-generated stub for MakeTransparent")]
        public string MakeTransparent()
        {
            throw new NotImplementedException("MakeTransparent is not fully implemented yet.");
        }
    }
}
