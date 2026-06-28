using System;
using LMKit.Agents.Tools;
using System.ComponentModel;
using Aspose.Words;

namespace LmKitOmniApi.Infrastructure.Tools.Office.Word
{
    public partial class WordTools
    {
        [LMFunction("Word_AddHeader", "Adds text to the header of the document.")]
        public string AddHeader([Description("Text to add to the header")] string text)
        {
            if (!_isOpen || _document == null) return "No document is open.";
            var builder = new DocumentBuilder(_document);
            builder.MoveToHeaderFooter(HeaderFooterType.HeaderPrimary);
            builder.Writeln(text);
            builder.MoveToDocumentEnd();
            return "Header added successfully.";
        }

        [LMFunction("Word_AddFooter", "Adds text to the footer of the document.")]
        public string AddFooter([Description("Text to add to the footer")] string text)
        {
            if (!_isOpen || _document == null) return "No document is open.";
            var builder = new DocumentBuilder(_document);
            builder.MoveToHeaderFooter(HeaderFooterType.FooterPrimary);
            builder.Writeln(text);
            builder.MoveToDocumentEnd();
            return "Footer added successfully.";
        }
    }
}
