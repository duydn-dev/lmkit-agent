using System;
using LMKit.Agents.Tools;
using Aspose.Cells;

namespace LmKitOmniApi.Infrastructure.Tools.Office.Excel
{
    /// <summary>
    /// Base class cho bộ công cụ thao tác với Microsoft Excel sử dụng Aspose.Cells.
    /// Bao gồm các tác vụ cơ bản về Workbook, Worksheet, Cell, Formatting, v.v.
    /// </summary>
    public partial class ExcelTools
    {
        // Các biến dùng chung để lưu trữ trạng thái tài liệu đang mở
        private string? _currentFilePath;
        private bool _isOpen = false;
        private Workbook? _workbook;

        public ExcelTools()
        {
        }
    }
}
