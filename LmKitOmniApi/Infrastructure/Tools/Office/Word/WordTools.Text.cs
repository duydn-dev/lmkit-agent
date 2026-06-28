using System;
using LMKit.Agents.Tools;
using System.ComponentModel;
using Aspose.Words;
using Aspose.Words.Replacing;

namespace LmKitOmniApi.Infrastructure.Tools.Office.Word
{
    public partial class WordTools
    {
        [LMFunction("Word_AppendText", "Appends text to the end of the document.")]
        public string AppendText([Description("The text to append")] string text)
        {
            if (!_isOpen || _document == null) return "No document is open.";
            var builder = new DocumentBuilder(_document);
            builder.MoveToDocumentEnd();
            builder.Write(text);
            return "Text appended successfully.";
        }

        [LMFunction("Word_DeleteText", "Deletes all occurrences of a specific text.")]
        public string DeleteText([Description("The text to delete")] string text)
        {
            if (!_isOpen || _document == null) return "No document is open.";
            _document.Range.Replace(text, "", new FindReplaceOptions());
            return "Text deleted successfully.";
        }

        [LMFunction("Word_ExtractText", "Extracts all text from the document.")]
        public string ExtractText()
        {
            if (!_isOpen || _document == null) return "No document is open.";
            return _document.ToString(SaveFormat.Text);
        }

        [LMFunction("Word_HighlightText", "Highlights all occurrences of a specific text with a color.")]
        public string HighlightText(
            [Description("The text to highlight")] string text, 
            [Description("The color name (e.g. 'Yellow', 'Red')")] string colorName)
        {
            if (!_isOpen || _document == null) return "No document is open.";
            var color = System.Drawing.Color.FromName(colorName);
            var options = new FindReplaceOptions();
            options.ApplyFont.HighlightColor = color;
            _document.Range.Replace(text, text, options);
            return "Text highlighted successfully.";
        }

        [LMFunction("Word_FindTextRegex", "Finds and replaces text using a Regular Expression.")]
        public string FindTextRegex(
            [Description("The regular expression pattern")] string pattern,
            [Description("The replacement string")] string replacement)
        {
            if (!_isOpen || _document == null) return "No document is open.";
            var regex = new System.Text.RegularExpressions.Regex(pattern);
            _document.Range.Replace(regex, replacement, new FindReplaceOptions());
            return "Regex replacement successful.";
        }
    }
}
