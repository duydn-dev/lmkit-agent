using System;
using System.ComponentModel;
using LMKit.Agents.Tools;
using ImageMagick;
using ImageMagick.Drawing;

namespace LmKitOmniApi.Infrastructure.Tools.Images
{
    public partial class ImageTools
    {
        [LMFunction("Image_AddText", "Adds text to the image.")]
        public string AddText(
            [Description("Text string")] string text,
            [Description("X coordinate")] double x,
            [Description("Y coordinate")] double y,
            [Description("Font size")] double fontSize,
            [Description("Text color")] string color,
            [Description("Font family (e.g. 'Arial')")] string fontFamily = "Arial")
        {
            if (!CheckIfOpen()) return "No image is open.";
            new Drawables()
                .Font(fontFamily)
                .FontPointSize(fontSize)
                .FillColor(new MagickColor(color))
                .Text(x, y, text)
                .Draw(_image);
            return $"Text added at ({x},{y}).";
        }

        [LMFunction("Image_ReplaceText", "Auto-generated stub for ReplaceText")]
        public string ReplaceText()
        {
            throw new NotImplementedException("ReplaceText is not fully implemented yet.");
        }

        [LMFunction("Image_RemoveText", "Auto-generated stub for RemoveText")]
        public string RemoveText()
        {
            throw new NotImplementedException("RemoveText is not fully implemented yet.");
        }

        [LMFunction("Image_MeasureText", "Auto-generated stub for MeasureText")]
        public string MeasureText()
        {
            throw new NotImplementedException("MeasureText is not fully implemented yet.");
        }

        [LMFunction("Image_SetFont", "Auto-generated stub for SetFont")]
        public string SetFont()
        {
            throw new NotImplementedException("SetFont is not fully implemented yet.");
        }

        [LMFunction("Image_SetFontSize", "Auto-generated stub for SetFontSize")]
        public string SetFontSize()
        {
            throw new NotImplementedException("SetFontSize is not fully implemented yet.");
        }

        [LMFunction("Image_SetFontColor", "Auto-generated stub for SetFontColor")]
        public string SetFontColor()
        {
            throw new NotImplementedException("SetFontColor is not fully implemented yet.");
        }

        [LMFunction("Image_SetTextAlignment", "Auto-generated stub for SetTextAlignment")]
        public string SetTextAlignment()
        {
            throw new NotImplementedException("SetTextAlignment is not fully implemented yet.");
        }

        [LMFunction("Image_RotateText", "Auto-generated stub for RotateText")]
        public string RotateText()
        {
            throw new NotImplementedException("RotateText is not fully implemented yet.");
        }

        [LMFunction("Image_WarpText", "Auto-generated stub for WarpText")]
        public string WarpText()
        {
            throw new NotImplementedException("WarpText is not fully implemented yet.");
        }
    }
}
