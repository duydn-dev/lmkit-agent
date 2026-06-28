using System;
using LMKit.Agents.Tools;
using System.ComponentModel;
using Aspose.Words;
using Aspose.Words.Tables;

namespace LmKitOmniApi.Infrastructure.Tools.Office.Word
{
    public partial class WordTools
    {
        [LMFunction("Word_CreateTable", "Creates a new table with specified rows and columns.")]
        public string CreateTable([Description("Number of rows")] int rows, [Description("Number of columns")] int cols)
        {
            if (!_isOpen || _document == null) return "No document is open.";
            var builder = new DocumentBuilder(_document);
            builder.MoveToDocumentEnd();
            var table = builder.StartTable();
            for (int r = 0; r < rows; r++)
            {
                for (int c = 0; c < cols; c++)
                {
                    builder.InsertCell();
                    builder.Write($"Row {r + 1}, Cell {c + 1}");
                }
                builder.EndRow();
            }
            builder.EndTable();
            return "Table created successfully.";
        }
    }
}
