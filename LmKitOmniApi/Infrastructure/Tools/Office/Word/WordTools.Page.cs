using System;
using LMKit.Agents.Tools;
using System.ComponentModel;
using Aspose.Words;
using System.Drawing;

namespace LmKitOmniApi.Infrastructure.Tools.Office.Word
{
    public partial class WordTools
    {
        [LMFunction("Word_InsertPageBreak", "Inserts a page break at the end of the document.")]
        public string InsertPageBreak()
        {
            if (!_isOpen || _document == null) return "No document is open.";
            var builder = new DocumentBuilder(_document);
            builder.MoveToDocumentEnd();
            builder.InsertBreak(BreakType.PageBreak);
            return "Page break inserted.";
        }

        [LMFunction("Word_SetPageColor", "Sets the background color of the pages.")]
        public string SetPageColor([Description("The color name (e.g. 'LightBlue')")] string colorName)
        {
            if (!_isOpen || _document == null) return "No document is open.";
            var color = Color.FromName(colorName);
            _document.PageColor = color;
            return $"Page color set to {colorName}.";
        }
    }
}
