namespace LmKitOmniApi.Application.Tenants;

/// <summary>Row của màn "Quản lý Tenant" — kèm số liệu sử dụng để admin đánh giá nhanh.</summary>
public sealed class TenantDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    /// <summary>Tên trợ lý AI hiển thị theo tenant (null → dùng mặc định "CILA Agent").</summary>
    public string? AgentDisplayName { get; set; }
    /// <summary>Có logo đã tải lên hay chưa — projection chỉ đọc cờ này, không nạp bytes.</summary>
    public bool HasLogo { get; set; }
    /// <summary>Mốc cập nhật logo (cache-buster cho URL ảnh phía client).</summary>
    public DateTime? LogoUpdatedAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public int UserCount { get; set; }
    public int DatabaseConnectionCount { get; set; }
}

/// <summary>Mục gọn cho dropdown chọn tenant (vd. gán kết nối CSDL).</summary>
public sealed class TenantOptionDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class SaveTenantRequest
{
    public string? Name { get; set; }
    /// <summary>Tên trợ lý AI hiển thị cho tenant. Rỗng/null → xóa override, dùng mặc định.</summary>
    public string? AgentDisplayName { get; set; }
}
