using System.Text;
using LmKitOmniApi.Application.Abstractions;
using Aspose.Words;
using Aspose.Cells;

namespace LmKitOmniApi.Infrastructure.Tools;

public class OfficeDocumentToolService : IOfficeDocumentToolService
{
    public async Task<string> ReadWordDocumentAsync(string filePath)
    {
        return await Task.Run(() =>
        {
            try
            {
                var doc = new Document(filePath);
                return doc.GetText();
            }
            catch (Exception ex)
            {
                return $"[Error reading Word document: {ex.Message}]";
            }
        });
    }

    public async Task<string> ReadExcelDocumentAsync(string filePath)
    {
        return await Task.Run(() =>
        {
            try
            {
                var workbook = new Workbook(filePath);
                var sb = new StringBuilder();
                
                foreach (Worksheet sheet in workbook.Worksheets)
                {
                    sb.AppendLine($"--- Sheet: {sheet.Name} ---");
                    var dataTable = sheet.Cells.ExportDataTableAsString(0, 0, sheet.Cells.MaxDataRow + 1, sheet.Cells.MaxDataColumn + 1, true);
                    
                    if (dataTable != null)
                    {
                        foreach (System.Data.DataRow row in dataTable.Rows)
                        {
                            var rowData = row.ItemArray.Select(item => item?.ToString() ?? "");
                            sb.AppendLine(string.Join(" | ", rowData));
                        }
                    }
                }
                return sb.ToString();
            }
            catch (Exception ex)
            {
                return $"[Error reading Excel document: {ex.Message}]";
            }
        });
    }
}
