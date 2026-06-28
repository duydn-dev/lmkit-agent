using System;
using LMKit.Agents.Tools;
using System.ComponentModel;
using Aspose.Words;

namespace LmKitOmniApi.Infrastructure.Tools.Office.Word
{
    public partial class WordTools
    {
        // Helper method to apply font settings to all text in the document
        private void ApplyFontToDocument(Action<Font> fontAction)
        {
            var nodes = _document.GetChildNodes(NodeType.Run, true);
            foreach (Run run in nodes)
            {
                fontAction(run.Font);
            }
        }

        [LMFunction("Word_SetFontName", "Sets the font name for the entire document.")]
        public string SetFontName([Description("The font name (e.g. 'Arial')")] string fontName)
        {
            if (!_isOpen || _document == null) return "No document is open.";
            ApplyFontToDocument(f => f.Name = fontName);
            return $"Font name set to {fontName}.";
        }

        [LMFunction("Word_SetFontSize", "Sets the font size for the entire document.")]
        public string SetFontSize([Description("The font size in points")] double size)
        {
            if (!_isOpen || _document == null) return "No document is open.";
            ApplyFontToDocument(f => f.Size = size);
            return $"Font size set to {size}.";
        }

        [LMFunction("Word_SetBold", "Sets whether the text is bold.")]
        public string SetBold([Description("True to set bold, false to remove")] bool isBold)
        {
            if (!_isOpen || _document == null) return "No document is open.";
            ApplyFontToDocument(f => f.Bold = isBold);
            return $"Bold set to {isBold}.";
        }

        [LMFunction("Word_SetItalic", "Sets whether the text is italic.")]
        public string SetItalic([Description("True to set italic, false to remove")] bool isItalic)
        {
            if (!_isOpen || _document == null) return "No document is open.";
            ApplyFontToDocument(f => f.Italic = isItalic);
            return $"Italic set to {isItalic}.";
        }

        [LMFunction("Word_SetUnderline", "Sets the underline style.")]
        public string SetUnderline([Description("True to underline, false to remove")] bool isUnderline)
        {
            if (!_isOpen || _document == null) return "No document is open.";
            ApplyFontToDocument(f => f.Underline = isUnderline ? Underline.Single : Underline.None);
            return $"Underline set to {isUnderline}.";
        }

        [LMFunction("Word_SetFontColor", "Sets the font color for the entire document.")]
        public string SetFontColor([Description("The color name (e.g. 'Red', 'Blue')")] string colorName)
        {
            if (!_isOpen || _document == null) return "No document is open.";
            var color = System.Drawing.Color.FromName(colorName);
            ApplyFontToDocument(f => f.Color = color);
            return $"Font color set to {colorName}.";
        }
    }
}
