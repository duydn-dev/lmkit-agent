using System;
using System.ComponentModel;
using LMKit.Agents.Tools;
using ImageMagick;
using ImageMagick.Drawing;

namespace LmKitOmniApi.Infrastructure.Tools.Images
{
    public partial class ImageTools
    {
        [LMFunction("Image_DrawLine", "Draws a line on the image.")]
        public string DrawLine(
            [Description("Starting X coordinate")] double startX,
            [Description("Starting Y coordinate")] double startY,
            [Description("Ending X coordinate")] double endX,
            [Description("Ending Y coordinate")] double endY,
            [Description("Color of the line (e.g. 'Red', '#FF0000')")] string color,
            [Description("Thickness of the line")] double width)
        {
            if (!CheckIfOpen()) return "No image is open.";
            new Drawables()
                .StrokeColor(new MagickColor(color))
                .StrokeWidth(width)
                .Line(startX, startY, endX, endY)
                .Draw(_image);
            return $"Drew line from ({startX},{startY}) to ({endX},{endY}) with color {color}.";
        }

        [LMFunction("Image_DrawRectangle", "Draws a rectangle on the image.")]
        public string DrawRectangle(
            [Description("Top-left X coordinate")] double x,
            [Description("Top-left Y coordinate")] double y,
            [Description("Width of the rectangle")] double width,
            [Description("Height of the rectangle")] double height,
            [Description("Border color")] string strokeColor,
            [Description("Border thickness")] double strokeWidth,
            [Description("Fill color (use 'Transparent' for no fill)")] string fillColor)
        {
            if (!CheckIfOpen()) return "No image is open.";
            new Drawables()
                .StrokeColor(new MagickColor(strokeColor))
                .StrokeWidth(strokeWidth)
                .FillColor(new MagickColor(fillColor))
                .Rectangle(x, y, x + width, y + height)
                .Draw(_image);
            return $"Drew rectangle at ({x},{y}) size {width}x{height}.";
        }

        [LMFunction("Image_DrawRoundedRectangle", "Auto-generated stub for DrawRoundedRectangle")]
        public string DrawRoundedRectangle()
        {
            throw new NotImplementedException("DrawRoundedRectangle is not fully implemented yet.");
        }

        [LMFunction("Image_DrawCircle", "Draws a circle on the image.")]
        public string DrawCircle(
            [Description("Center X coordinate")] double centerX,
            [Description("Center Y coordinate")] double centerY,
            [Description("Radius of the circle")] double radius,
            [Description("Border color")] string strokeColor,
            [Description("Border thickness")] double strokeWidth,
            [Description("Fill color (use 'Transparent' for no fill)")] string fillColor)
        {
            if (!CheckIfOpen()) return "No image is open.";
            new Drawables()
                .StrokeColor(new MagickColor(strokeColor))
                .StrokeWidth(strokeWidth)
                .FillColor(new MagickColor(fillColor))
                .Circle(centerX, centerY, centerX + radius, centerY)
                .Draw(_image);
            return $"Drew circle at ({centerX},{centerY}) with radius {radius}.";
        }

        [LMFunction("Image_DrawEllipse", "Auto-generated stub for DrawEllipse")]
        public string DrawEllipse()
        {
            throw new NotImplementedException("DrawEllipse is not fully implemented yet.");
        }

        [LMFunction("Image_DrawPolygon", "Auto-generated stub for DrawPolygon")]
        public string DrawPolygon()
        {
            throw new NotImplementedException("DrawPolygon is not fully implemented yet.");
        }

        [LMFunction("Image_DrawBezierCurve", "Auto-generated stub for DrawBezierCurve")]
        public string DrawBezierCurve()
        {
            throw new NotImplementedException("DrawBezierCurve is not fully implemented yet.");
        }

        [LMFunction("Image_DrawArrow", "Auto-generated stub for DrawArrow")]
        public string DrawArrow()
        {
            throw new NotImplementedException("DrawArrow is not fully implemented yet.");
        }

        [LMFunction("Image_DrawText", "Draws text on the image.")]
        public string DrawText(
            [Description("Text to draw")] string text,
            [Description("X coordinate")] double x,
            [Description("Y coordinate")] double y,
            [Description("Font size")] double fontSize,
            [Description("Text color")] string color)
        {
            if (!CheckIfOpen()) return "No image is open.";
            new Drawables()
                .FontPointSize(fontSize)
                .FillColor(new MagickColor(color))
                .Text(x, y, text)
                .Draw(_image);
            return $"Drew text '{text}' at ({x},{y}) with size {fontSize}.";
        }

        [LMFunction("Image_DrawWatermark", "Auto-generated stub for DrawWatermark")]
        public string DrawWatermark()
        {
            throw new NotImplementedException("DrawWatermark is not fully implemented yet.");
        }

        [LMFunction("Image_DrawImage", "Auto-generated stub for DrawImage")]
        public string DrawImage()
        {
            throw new NotImplementedException("DrawImage is not fully implemented yet.");
        }

        [LMFunction("Image_DrawQRCode", "Auto-generated stub for DrawQRCode")]
        public string DrawQRCode()
        {
            throw new NotImplementedException("DrawQRCode is not fully implemented yet.");
        }

        [LMFunction("Image_DrawBarcode", "Auto-generated stub for DrawBarcode")]
        public string DrawBarcode()
        {
            throw new NotImplementedException("DrawBarcode is not fully implemented yet.");
        }

        [LMFunction("Image_FillRectangle", "Auto-generated stub for FillRectangle")]
        public string FillRectangle()
        {
            throw new NotImplementedException("FillRectangle is not fully implemented yet.");
        }

        [LMFunction("Image_FillCircle", "Auto-generated stub for FillCircle")]
        public string FillCircle()
        {
            throw new NotImplementedException("FillCircle is not fully implemented yet.");
        }

        [LMFunction("Image_DrawGrid", "Auto-generated stub for DrawGrid")]
        public string DrawGrid()
        {
            throw new NotImplementedException("DrawGrid is not fully implemented yet.");
        }

        [LMFunction("Image_DrawBorder", "Auto-generated stub for DrawBorder")]
        public string DrawBorder()
        {
            throw new NotImplementedException("DrawBorder is not fully implemented yet.");
        }

        [LMFunction("Image_DrawShadow", "Auto-generated stub for DrawShadow")]
        public string DrawShadow()
        {
            throw new NotImplementedException("DrawShadow is not fully implemented yet.");
        }

        [LMFunction("Image_DrawGradient", "Auto-generated stub for DrawGradient")]
        public string DrawGradient()
        {
            throw new NotImplementedException("DrawGradient is not fully implemented yet.");
        }

        [LMFunction("Image_DrawPattern", "Auto-generated stub for DrawPattern")]
        public string DrawPattern()
        {
            throw new NotImplementedException("DrawPattern is not fully implemented yet.");
        }
    }
}
