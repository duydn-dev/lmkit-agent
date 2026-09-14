namespace LmKitOmniApi.Infrastructure.AI.Documents;

/// <summary>
/// Nạp license Aspose (Words + Cells) đúng MỘT lần cho cả tiến trình, thread-safe.
/// Đường dẫn file .lic đến từ cấu hình <c>OfficeAuthoring:AsposeLicensePath</c>
/// (file license KHÔNG commit vào repo — mount/copy khi triển khai). Thiếu hoặc
/// hỏng license thì Aspose vẫn chạy ở CHẾ ĐỘ ĐÁNH GIÁ (chèn watermark/sheet
/// "Evaluation Warning") — service nối cảnh báo vào thông điệp trả về chứ không
/// chặn, vì file có watermark vẫn hơn là không có file.
/// </summary>
public static class AsposeLicensing
{
    private static readonly object Gate = new();
    private static bool _attempted;
    private static bool _wordsLicensed;
    private static bool _cellsLicensed;

    /// <summary>true khi CẢ Words lẫn Cells đã nạp license thành công.</summary>
    public static bool EnsureApplied(string? licensePath, ILogger logger)
    {
        lock (Gate)
        {
            if (_attempted) return _wordsLicensed && _cellsLicensed;
            _attempted = true;

            if (string.IsNullOrWhiteSpace(licensePath))
            {
                logger.LogWarning(
                    "📄 [Aspose] Chưa cấu hình OfficeAuthoring:AsposeLicensePath — chạy chế độ đánh giá (file có watermark).");
                return false;
            }
            if (!File.Exists(licensePath))
            {
                logger.LogError(
                    "📄 [Aspose] Không tìm thấy file license tại {Path} — chạy chế độ đánh giá.", licensePath);
                return false;
            }

            // Nạp từng sản phẩm riêng: một bên hỏng không kéo bên kia xuống.
            try
            {
                new Aspose.Words.License().SetLicense(licensePath);
                _wordsLicensed = true;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "📄 [Aspose] Nạp license Aspose.Words thất bại — Words chạy chế độ đánh giá.");
            }

            try
            {
                new Aspose.Cells.License().SetLicense(licensePath);
                _cellsLicensed = true;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "📄 [Aspose] Nạp license Aspose.Cells thất bại — Cells chạy chế độ đánh giá.");
            }

            if (_wordsLicensed && _cellsLicensed)
                logger.LogInformation("📄 [Aspose] Đã nạp license Words + Cells từ {Path}.", licensePath);
            return _wordsLicensed && _cellsLicensed;
        }
    }

    /// <summary>Test seam: reset trạng thái nạp (chỉ dùng trong unit test).</summary>
    internal static void ResetForTests()
    {
        lock (Gate)
        {
            _attempted = false;
            _wordsLicensed = false;
            _cellsLicensed = false;
        }
    }
}
