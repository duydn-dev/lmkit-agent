using System;
using System.IO;
using LMKit.Agents.Tools;
using ImageMagick;

namespace LmKitOmniApi.Infrastructure.Tools.Images
{
    /// <summary>
    /// Base class cho bộ công cụ thao tác với Hình ảnh.
    /// Bao gồm các tác vụ cơ bản về xử lý ảnh, biến đổi, vẽ, filter, v.v.
    /// </summary>
    public partial class ImageTools : IDisposable
    {
        private MagickImage? _image;
        private string? _currentFilePath;
        private bool _isOpen = false;

        public ImageTools()
        {
        }

        private bool CheckIfOpen()
        {
            return _isOpen && _image != null;
        }

        public void Dispose()
        {
            _image?.Dispose();
            _image = null;
            _isOpen = false;
        }
    }
}
