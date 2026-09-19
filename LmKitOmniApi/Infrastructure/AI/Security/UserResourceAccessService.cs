namespace LmKitOmniApi.Infrastructure.AI.Security;

/// <summary>Maps authenticated tenant/user identities to an isolated upload root.</summary>
public sealed class UserResourceAccessService
{
    private readonly ToolSandboxService _sandbox;

    public UserResourceAccessService(ToolSandboxService sandbox) => _sandbox = sandbox;

    public string GetUploadDirectory(Guid tenantId, Guid userId) => Path.Combine(
        Directory.GetCurrentDirectory(),
        "Uploads",
        tenantId.ToString("N"),
        userId.ToString("N"));

    public PathValidationResult ValidateOwnedPath(Guid tenantId, Guid userId, string requestedPath)
    {
        var sandboxResult = _sandbox.ValidateFilePath(requestedPath);
        if (!sandboxResult.IsAllowed) return sandboxResult;

        var ownerRoot = Path.GetFullPath(GetUploadDirectory(tenantId, userId));
        var relative = Path.GetRelativePath(ownerRoot, sandboxResult.SanitizedPath);
        var owned = relative != ".."
            && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            && !Path.IsPathRooted(relative);

        return owned
            ? sandboxResult
            : PathValidationResult.Deny("Resource does not belong to the authenticated user.");
    }

    /// <summary>
    /// Tên lưu trữ trên đĩa cho file do agent tạo ra (run_python, soạn thảo Office,
    /// screenshot ComputerUse): <c>[ten-goc-da-lam-sach]-[guid32][ext]</c>.
    ///
    /// Vì sao có cả hai thành phần: GUID thuần khiến người dùng soi thư mục Uploads
    /// không thể nhận ra file prompt của mình đã yêu cầu (timeout.txt hóa thành
    /// 32-hex vô nghĩa) — đó là lỗi sản phẩm. Giữ stem gốc khôi phục ngữ nghĩa, còn
    /// đoạn GUID giữ trọn ưu điểm bảo mật của tên server-sinh: id tải xuống vẫn là
    /// chuỗi không đoán được, hai lần chạy cùng đặt một tên không đụng nhau. Tên
    /// hiển thị trên UI đi riêng qua <see cref="ProducedFile.Name"/> nên không cơ
    /// chế nào phụ thuộc format này — chỉ stem gốc là mới.
    /// Stem làm sạch: chỉ giữ chữ/số/-/_/. (các ký tự khác gộp thành '-'), cắt
    /// chấm/gạch hai đầu (Windows bỏ đuôi chấm khi ghi), tối đa 40 ký tự; trống
    /// thì dùng "file". Extension lấy từ tên gốc, rỗng thì dùng fallback.
    /// </summary>
    public static string BuildStoredFileName(string? originalName, string fallbackExtension)
    {
        var extension = Path.GetExtension(originalName ?? string.Empty);
        if (string.IsNullOrWhiteSpace(extension))
            extension = string.IsNullOrWhiteSpace(fallbackExtension) ? string.Empty : fallbackExtension;
        if (!extension.StartsWith('.')) extension = "." + extension;
        extension = extension.ToLowerInvariant();

        var stem = new System.Text.StringBuilder();
        foreach (var ch in Path.GetFileNameWithoutExtension(originalName ?? string.Empty))
        {
            var safe = char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.' ? ch : '-';
            if (safe == '-' && (stem.Length == 0 || stem[^1] == '-')) continue; // không dẫn đầu/không lặp
            stem.Append(safe);
        }

        var cleaned = stem.ToString().Trim('.', '-');
        if (cleaned.Length > 40) cleaned = cleaned[..40].Trim('.', '-');
        if (cleaned.Length == 0) cleaned = "file";

        return $"{cleaned}-{Guid.NewGuid():N}{extension}";
    }
}
