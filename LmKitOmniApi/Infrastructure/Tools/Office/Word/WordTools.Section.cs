using System;
using LMKit.Agents.Tools;
using System.ComponentModel;
using Aspose.Words;

namespace LmKitOmniApi.Infrastructure.Tools.Office.Word
{
    public partial class WordTools
    {
        [LMFunction("Word_AddSection", "Adds a new section to the end of the document.")]
        public string AddSection()
        {
            if (!_isOpen || _document == null) return "No document is open.";
            var builder = new DocumentBuilder(_document);
            builder.MoveToDocumentEnd();
            builder.InsertBreak(BreakType.SectionBreakNewPage);
            return "New section added.";
        }

        [LMFunction("Word_SetSectionMargins", "Sets the margins for a specific section.")]
        public string SetSectionMargins(
            [Description("The 0-based index of the section (-1 for all)")] int sectionIndex,
            [Description("Left margin in points")] double left,
            [Description("Right margin in points")] double right,
            [Description("Top margin in points")] double top,
            [Description("Bottom margin in points")] double bottom)
        {
            if (!_isOpen || _document == null) return "No document is open.";
            
            if (sectionIndex == -1)
            {
                foreach (Section sec in _document.Sections)
                {
                    sec.PageSetup.LeftMargin = left;
                    sec.PageSetup.RightMargin = right;
                    sec.PageSetup.TopMargin = top;
                    sec.PageSetup.BottomMargin = bottom;
                }
                return "Margins set for all sections.";
            }
            
            if (sectionIndex >= 0 && sectionIndex < _document.Sections.Count)
            {
                var setup = _document.Sections[sectionIndex].PageSetup;
                setup.LeftMargin = left;
                setup.RightMargin = right;
                setup.TopMargin = top;
                setup.BottomMargin = bottom;
                return $"Margins set for section {sectionIndex}.";
            }
            return "Section index out of range.";
        }

        [LMFunction("Word_SetSectionOrientation", "Sets the orientation (Landscape/Portrait) for a specific section.")]
        public string SetSectionOrientation(
            [Description("The 0-based index of the section (-1 for all)")] int sectionIndex,
            [Description("Orientation: 'Portrait' or 'Landscape'")] string orientation)
        {
            if (!_isOpen || _document == null) return "No document is open.";
            
            Orientation orient = Enum.TryParse(orientation, true, out Orientation o) ? o : Orientation.Portrait;

            if (sectionIndex == -1)
            {
                foreach (Section sec in _document.Sections)
                    sec.PageSetup.Orientation = orient;
                return $"Orientation set to {orient} for all sections.";
            }
            
            if (sectionIndex >= 0 && sectionIndex < _document.Sections.Count)
            {
                _document.Sections[sectionIndex].PageSetup.Orientation = orient;
                return $"Orientation set to {orient} for section {sectionIndex}.";
            }
            return "Section index out of range.";
        }
    }
}
