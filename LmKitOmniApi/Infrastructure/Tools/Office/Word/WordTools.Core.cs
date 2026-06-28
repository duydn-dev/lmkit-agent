using System;
using LMKit.Agents.Tools;
using System.ComponentModel;
using Aspose.Words;
using Aspose.Words.Replacing;

namespace LmKitOmniApi.Infrastructure.Tools.Office.Word
{
    public partial class WordTools
    {
        [LMFunction("Word_CreateDocument", "Create a new Microsoft Word document and save it to the specified path.")]
        public string CreateDocument([Description("The absolute path where the new .docx file will be saved")] string filePath)
        {
            _document = new Document();
            _document.Save(filePath);
            _currentFilePath = filePath;
            _isOpen = true;
            return $"Successfully created document at {filePath}";
        }

        [LMFunction("Word_OpenDocument", "Open an existing Microsoft Word document.")]
        public string OpenDocument([Description("The absolute path of the .docx file to open")] string filePath)
        {
            _document = new Document(filePath);
            _currentFilePath = filePath;
            _isOpen = true;
            return $"Successfully opened document from {filePath}";
        }

        [LMFunction("Word_SaveDocument", "Save the currently opened Microsoft Word document.")]
        public string SaveDocument()
        {
            if (!_isOpen || _document == null || _currentFilePath == null) return "No document is currently open.";
            _document.Save(_currentFilePath);
            return "Document saved successfully.";
        }

        [LMFunction("Word_SaveAs", "Save the currently opened Microsoft Word document to a new path.")]
        public string SaveAs([Description("The new absolute path to save the .docx file")] string newFilePath)
        {
            if (!_isOpen || _document == null) return "No document is currently open.";
            _document.Save(newFilePath);
            _currentFilePath = newFilePath;
            return $"Document saved to {newFilePath}";
        }

        [LMFunction("Word_InsertText", "Insert a new paragraph of text at the end of the document.")]
        public string InsertText([Description("The text content to insert")] string text)
        {
            if (!_isOpen || _document == null) return "No document is currently open.";
            var builder = new DocumentBuilder(_document);
            builder.MoveToDocumentEnd();
            builder.Writeln(text);
            return "Text inserted successfully.";
        }
        
        [LMFunction("Word_ReplaceText", "Replace all occurrences of a specific text with a new text.")]
        public string ReplaceText([Description("The text to find")] string oldText, [Description("The text to replace it with")] string newText)
        {
            if (!_isOpen || _document == null) return "No document is currently open.";
            _document.Range.Replace(oldText, newText, new FindReplaceOptions());
            return "Text replaced successfully.";
        }
    }
}
