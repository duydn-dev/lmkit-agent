using System;
using LMKit.Agents.Tools;
using Aspose.Words;

namespace LmKitOmniApi.Infrastructure.Tools.Office.Word
{
    /// <summary>
    /// Base class cho bộ công cụ thao tác với Microsoft Word sử dụng Aspose.Words.
    /// Bao gồm các tác vụ cơ bản về Document, Text, Formatting, Table, Image, v.v.
    /// </summary>
    public partial class WordTools
    {
        // Các biến dùng chung để lưu trữ trạng thái tài liệu đang mở
        private string? _currentFilePath;
        private bool _isOpen = false;
        private Document? _document;

        public WordTools()
        {
        }
    }
}
