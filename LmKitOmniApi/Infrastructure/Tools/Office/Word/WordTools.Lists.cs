using System;
using LMKit.Agents.Tools;
using System.ComponentModel;
using Aspose.Words;
using Aspose.Words.Lists;

namespace LmKitOmniApi.Infrastructure.Tools.Office.Word
{
    public partial class WordTools
    {
        [LMFunction("Word_CreateBulletList", "Creates a bullet list from an array of strings.")]
        public string CreateBulletList([Description("Comma-separated items")] string items)
        {
            if (!_isOpen || _document == null) return "No document is open.";
            var builder = new DocumentBuilder(_document);
            builder.MoveToDocumentEnd();
            builder.ListFormat.ApplyBulletDefault();
            
            var arr = items.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            foreach(var item in arr)
            {
                builder.Writeln(item.Trim());
            }
            builder.ListFormat.RemoveNumbers();
            return "Bullet list created successfully.";
        }

        [LMFunction("Word_CreateNumberList", "Creates a numbered list from an array of strings.")]
        public string CreateNumberList([Description("Comma-separated items")] string items)
        {
            if (!_isOpen || _document == null) return "No document is open.";
            var builder = new DocumentBuilder(_document);
            builder.MoveToDocumentEnd();
            builder.ListFormat.ApplyNumberDefault();
            
            var arr = items.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            foreach(var item in arr)
            {
                builder.Writeln(item.Trim());
            }
            builder.ListFormat.RemoveNumbers();
            return "Numbered list created successfully.";
        }
    }
}
