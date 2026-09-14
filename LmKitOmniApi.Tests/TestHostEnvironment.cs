using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace LmKitOmniApi.Tests;

/// <summary>
/// IHostEnvironment tối giản cho unit test: ContentRootPath trỏ tới một thư mục
/// tạm DUY NHẤT mỗi lần khởi tạo (mặc định KHÔNG có thư mục con "Aspose"), để
/// OfficeAuthoringService dò license trả null → chạy chế độ đánh giá một cách
/// tất định, không vô tình nhặt license nằm trong cây mã nguồn thật.
/// Truyền <paramref name="contentRoot"/> khi test muốn tự dựng thư mục Aspose/.
/// </summary>
public sealed class TestHostEnvironment : IHostEnvironment
{
    public TestHostEnvironment(string? contentRoot = null)
    {
        ContentRootPath = contentRoot
            ?? Path.Combine(Path.GetTempPath(), "lmkit-tests-env", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(ContentRootPath);
        ContentRootFileProvider = new PhysicalFileProvider(ContentRootPath);
    }

    public string EnvironmentName { get; set; } = "Testing";
    public string ApplicationName { get; set; } = "LmKitOmniApi.Tests";
    public string ContentRootPath { get; set; }
    public IFileProvider ContentRootFileProvider { get; set; }
}
