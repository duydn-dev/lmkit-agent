using LmKitOmniApi.Infrastructure.AI.Database;
using MediatR;

namespace LmKitOmniApi.Application.DatabaseConnections.Queries;

/// <summary>
/// Schema diagram của một kết nối CSDL ngoài: bảng + cột + khoá, cùng các quan hệ
/// khoá ngoại đã tách thành cạnh (from → to) để client vẽ ER diagram.
///
/// Đọc schema SỐNG qua <see cref="ExternalDatabaseService.IntrospectAsync"/> — đúng
/// phép introspection mà chỉ mục schema dùng, đi qua egress + read-only như mọi
/// thao tác CSDL khác. Không trả chuỗi kết nối.
/// </summary>
public sealed class GetDatabaseSchemaQuery : IRequest<DatabaseSchemaResult>
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
}

public sealed class DatabaseSchemaResult
{
    public bool Success { get; init; }
    public bool NotFound { get; init; }
    public string? Error { get; init; }
    public DatabaseSchemaDto? Schema { get; init; }

    public static DatabaseSchemaResult Ok(DatabaseSchemaDto schema) => new() { Success = true, Schema = schema };

    public static DatabaseSchemaResult Fail(string error) => new() { Error = error };

    public static DatabaseSchemaResult Missing() =>
        new() { NotFound = true, Error = "Không tìm thấy kết nối." };
}

public sealed class DatabaseSchemaDto
{
    public Guid ConnectionId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Provider { get; set; } = string.Empty;
    public bool IsActive { get; set; }

    /// <summary>Trạng thái chỉ mục schema của kết nối — diagram đọc sống nên vẫn xem được khi chưa index.</summary>
    public bool IsIndexed { get; set; }
    public string IndexStatus { get; set; } = string.Empty;
    public DateTime? LastIndexedAtUtc { get; set; }

    /// <summary>Số bảng thực sự nằm trong diagram (đã áp trần hiển thị).</summary>
    public int TableCount { get; set; }

    /// <summary>Số bảng introspection trả về, trước khi áp trần.</summary>
    public int TotalTableCount { get; set; }

    /// <summary>True khi có bảng bị cắt do vượt trần — client phải nói rõ, không im lặng.</summary>
    public bool Truncated { get; set; }

    public IReadOnlyList<DbDiagramTable> Tables { get; set; } = [];
    public IReadOnlyList<DbDiagramRelation> Relations { get; set; } = [];
}
