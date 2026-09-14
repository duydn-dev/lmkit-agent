namespace LmKitOmniApi.Application.Tenants;

/// <summary>Row của màn "Quản lý Tenant" — kèm số liệu sử dụng để admin đánh giá nhanh.</summary>
public sealed class TenantDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
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
}
