using LmKitOmniApi.Application.Abstractions;

namespace LmKitOmniApi.Infrastructure.Tools;

public class OfficeDocumentToolService : IOfficeDocumentToolService
{
    public async Task<string> ReadWordDocumentAsync(string filePath)
    {
        // Mock Implementation: In a real app, use Aspose.Words
        await Task.Delay(100);
        return $"[Content of Word Document: {filePath}]\nMocked text extracted from the document.";
    }

    public async Task<string> ReadExcelDocumentAsync(string filePath)
    {
        // Mock Implementation: In a real app, use Aspose.Cells
        await Task.Delay(100);
        return $"[Content of Excel Document: {filePath}]\nMocked structured data from the spreadsheet.";
    }
}
