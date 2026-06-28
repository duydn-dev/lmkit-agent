using System;
using LMKit.Agents.Tools;
using System.ComponentModel;
using Aspose.Words;

namespace LmKitOmniApi.Infrastructure.Tools.Office.Word
{
    public partial class WordTools
    {
        [LMFunction("Word_AddParagraph", "Adds a new paragraph at the end of the document.")]
        public string AddParagraph([Description("The text for the paragraph")] string text)
        {
            if (!_isOpen || _document == null) return "No document is open.";
            var builder = new DocumentBuilder(_document);
            builder.MoveToDocumentEnd();
            builder.Writeln(text);
            return "Paragraph added successfully.";
        }

        [LMFunction("Word_DeleteParagraph", "Deletes the paragraph at the specified index.")]
        public string DeleteParagraph([Description("The 0-based index of the paragraph to delete")] int index)
        {
            if (!_isOpen || _document == null) return "No document is open.";
            var paragraphs = _document.GetChildNodes(NodeType.Paragraph, true);
            if (index >= 0 && index < paragraphs.Count)
            {
                paragraphs[index].Remove();
                return "Paragraph deleted successfully.";
            }
            return "Paragraph index out of range.";
        }

        [LMFunction("Word_SetParagraphAlignment", "Sets the alignment of a specific paragraph.")]
        public string SetParagraphAlignment(
            [Description("The 0-based index of the paragraph (-1 for all)")] int index,
            [Description("Alignment: 'Left', 'Center', 'Right', 'Justify'")] string alignment)
        {
            if (!_isOpen || _document == null) return "No document is open.";
            
            ParagraphAlignment align = ParagraphAlignment.Left;
            if (Enum.TryParse(alignment, true, out ParagraphAlignment parsedAlign))
                align = parsedAlign;

            var paragraphs = _document.GetChildNodes(NodeType.Paragraph, true);
            if (index == -1)
            {
                foreach (Paragraph p in paragraphs) p.ParagraphFormat.Alignment = align;
                return $"All paragraphs aligned to {align}.";
            }
            else if (index >= 0 && index < paragraphs.Count)
            {
                ((Paragraph)paragraphs[index]).ParagraphFormat.Alignment = align;
                return $"Paragraph {index} aligned to {align}.";
            }
            return "Paragraph index out of range.";
        }

        [LMFunction("Word_SetParagraphSpacing", "Sets the spacing before and after a paragraph.")]
        public string SetParagraphSpacing(
            [Description("The 0-based index of the paragraph (-1 for all)")] int index,
            [Description("Spacing before in points")] double spaceBefore,
            [Description("Spacing after in points")] double spaceAfter)
        {
            if (!_isOpen || _document == null) return "No document is open.";
            
            var paragraphs = _document.GetChildNodes(NodeType.Paragraph, true);
            if (index == -1)
            {
                foreach (Paragraph p in paragraphs) 
                {
                    p.ParagraphFormat.SpaceBefore = spaceBefore;
                    p.ParagraphFormat.SpaceAfter = spaceAfter;
                }
                return $"Spacing set for all paragraphs.";
            }
            else if (index >= 0 && index < paragraphs.Count)
            {
                var p = (Paragraph)paragraphs[index];
                p.ParagraphFormat.SpaceBefore = spaceBefore;
                p.ParagraphFormat.SpaceAfter = spaceAfter;
                return $"Spacing set for paragraph {index}.";
            }
            return "Paragraph index out of range.";
        }
    }
}
