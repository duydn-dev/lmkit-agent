using System;
using LMKit.Agents.Tools;
using System.ComponentModel;
using Aspose.Words;

namespace LmKitOmniApi.Infrastructure.Tools.Office.Word
{
    public partial class WordTools
    {
        [LMFunction("Word_SaveAsPdf", "Converts and saves the current document as a PDF.")]
        public string SaveAsPdf([Description("The absolute path to save the .pdf file")] string pdfPath)
        {
            if (!_isOpen || _document == null) return "No document is open.";
            _document.Save(pdfPath, SaveFormat.Pdf);
            return $"Document saved as PDF to {pdfPath}";
        }
    }
}
