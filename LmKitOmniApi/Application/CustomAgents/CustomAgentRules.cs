using LmKitOmniApi.Application.CustomAgents.Commands;
using LmKitOmniApi.Application.CustomAgents.Queries;
using LmKitOmniApi.Domain.Entities;
using LmKitOmniApi.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace LmKitOmniApi.Application.CustomAgents;

/// <summary>
/// Shared validation, normalization and DTO mapping for custom agents
/// (Gems/GPTs style personas). Create and update run the exact same rules.
///
/// CSV columns on <see cref="CustomAgent"/> are an implementation detail of the
/// entity — every API boundary works with parsed arrays, and this class is the
/// single place that serializes/parses them.
/// </summary>
public static class CustomAgentRules
{
    public const int MaxAgentsPerUser = 20;
    public const int MaxNameLength = 80;
    public const int MaxDescriptionLength = 300;
    public const int MaxIconLength = 16;
    public const int MaxPersonaPromptLength = 4000;
    public const int MaxAllowedTools = 20;
    public const int MaxKnowledgeDocuments = 10;

    /// <summary>
    /// The selectable tool catalog: the permission tool names actually enforced by
    /// ToolPermissionService / AgentOrchestrator.ActionToToolMap. Labels and
    /// descriptions are user-facing Vietnamese. Safe default tools (máy tính,
    /// ngày giờ, phân tích JSON/CSV/XML) are always available to every agent and
    /// are intentionally NOT part of this whitelist. "SUMMARIZE" shares the
    /// AnalyzeText permission, so selecting "AnalyzeText" also enables tóm tắt.
    /// Dynamic MCP tools are excluded whenever a whitelist is set (admin surface).
    /// </summary>
    public static readonly IReadOnlyList<CustomAgentToolDto> ToolCatalog = new List<CustomAgentToolDto>
    {
        new()
        {
            Name = "QueryKnowledgeBase",
            Label = "Tri thức nội bộ",
            Description = "Truy vấn kho tri thức và tài liệu đã tải lên (RAG)."
        },
        new()
        {
            Name = "AnalyzeText",
            Label = "Phân tích văn bản",
            Description = "Phân tích cảm xúc, trích xuất thực thể và tóm tắt nội dung."
        },
        new()
        {
            Name = "RunCode",
            Label = "Chạy mã JavaScript",
            Description = "Thực thi đoạn mã JavaScript ngắn trong sandbox an toàn để tính toán và biến đổi dữ liệu (không mạng, không truy cập tệp)."
        },
        new()
        {
            Name = "AnalyzeImage",
            Label = "Phân tích hình ảnh",
            Description = "Đọc OCR và phân tích nội dung hình ảnh được phép truy cập."
        },
        new()
        {
            Name = "TranscribeAudio",
            Label = "Chuyển giọng nói thành văn bản",
            Description = "Nhận dạng và phiên âm tệp âm thanh được phép truy cập."
        },
        new()
        {
            Name = "SearchWeb",
            Label = "Tìm kiếm web",
            Description = "Tìm kiếm thông tin mới nhất từ các nguồn web được phê duyệt."
        },
        new()
        {
            Name = "Delegate",
            Label = "Ủy quyền chuyên gia",
            Description = "Chuyển yêu cầu phức tạp cho các agent chuyên môn (nghiên cứu, phân tích, hình ảnh)."
        },
    };

    // Canonical-casing lookup so stored CSV always uses the catalog's exact names.
    private static readonly Dictionary<string, string> CanonicalToolNames =
        ToolCatalog.ToDictionary(tool => tool.Name, tool => tool.Name, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Validates a create/update payload. Returns the exact Vietnamese error
    /// message for a 400 response, or null when the payload is valid.
    /// The knowledge-document check is tenant+owner scoped: every id must be a
    /// document owned by the caller inside the caller's tenant.
    /// </summary>
    public static async Task<string?> ValidateAsync(
        HermesDbContext db,
        SaveCustomAgentCommandBase request,
        CancellationToken ct)
    {
        var name = request.Name?.Trim();
        if (string.IsNullOrEmpty(name))
            return "Tên agent là bắt buộc.";
        if (name.Length > MaxNameLength)
            return $"Tên agent không được vượt quá {MaxNameLength} ký tự.";

        if (request.Description?.Trim() is { Length: > MaxDescriptionLength })
            return $"Mô tả không được vượt quá {MaxDescriptionLength} ký tự.";
        if (request.Icon?.Trim() is { Length: > MaxIconLength })
            return $"Icon không được vượt quá {MaxIconLength} ký tự.";

        var persona = request.PersonaPrompt?.Trim();
        if (string.IsNullOrEmpty(persona))
            return "Persona (system prompt) là bắt buộc.";
        if (persona.Length > MaxPersonaPromptLength)
            return $"Persona không được vượt quá {MaxPersonaPromptLength} ký tự.";

        if (request.AllowedTools is { } tools)
        {
            if (tools.Count > MaxAllowedTools)
                return $"Mỗi agent chỉ được chọn tối đa {MaxAllowedTools} công cụ.";
            foreach (var tool in tools)
            {
                if (string.IsNullOrWhiteSpace(tool) || !CanonicalToolNames.ContainsKey(tool.Trim()))
                    return $"Công cụ '{tool}' không hợp lệ.";
            }
        }

        var documentIds = NormalizeDocumentIds(request.KnowledgeDocumentIds);
        if (documentIds.Count > MaxKnowledgeDocuments)
            return $"Mỗi agent chỉ được gắn tối đa {MaxKnowledgeDocuments} tài liệu tri thức.";
        if (documentIds.Count > 0)
        {
            var ownedCount = await db.Documents.CountAsync(
                document => documentIds.Contains(document.Id)
                    && document.UserId == request.UserId
                    && document.User!.TenantId == request.TenantId,
                ct);
            if (ownedCount != documentIds.Count)
                return "Một hoặc nhiều tài liệu không tồn tại hoặc không thuộc quyền sở hữu của bạn.";
        }

        return null;
    }

    /// <summary>Drops empty guids and duplicates while preserving order.</summary>
    public static List<Guid> NormalizeDocumentIds(List<Guid>? ids) =>
        ids?.Where(id => id != Guid.Empty).Distinct().ToList() ?? new List<Guid>();

    /// <summary>Optional free-text field normalization: whitespace-only becomes null, otherwise trimmed.</summary>
    public static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>
    /// Serializes a validated tool whitelist to the entity CSV. Null AND empty
    /// both become null CSV — per the entity contract, "null/empty = the caller
    /// role's default tool set".
    /// </summary>
    public static string? BuildToolsCsv(List<string>? allowedTools)
    {
        if (allowedTools is null) return null;
        var canonical = allowedTools
            .Where(tool => !string.IsNullOrWhiteSpace(tool))
            .Select(tool => CanonicalToolNames[tool.Trim()])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return canonical.Count == 0 ? null : string.Join(",", canonical);
    }

    /// <summary>Serializes validated document ids to the entity CSV (null when empty).</summary>
    public static string? BuildDocumentIdsCsv(List<Guid>? ids)
    {
        var normalized = NormalizeDocumentIds(ids);
        return normalized.Count == 0 ? null : string.Join(",", normalized.Select(id => id.ToString()));
    }

    /// <summary>
    /// Parses the stored tool CSV. Null/whitespace (role-default semantics)
    /// returns null; a non-null result is a non-empty, de-duplicated whitelist.
    /// </summary>
    public static IReadOnlyList<string>? ParseToolsCsv(string? csv)
    {
        if (string.IsNullOrWhiteSpace(csv)) return null;
        var tools = csv
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return tools.Count == 0 ? null : tools;
    }

    /// <summary>
    /// Parses the stored knowledge-document CSV. Unparseable/empty entries are
    /// skipped; null is returned when nothing valid remains.
    /// </summary>
    public static IReadOnlyList<Guid>? ParseDocumentIdsCsv(string? csv)
    {
        if (string.IsNullOrWhiteSpace(csv)) return null;
        var ids = new List<Guid>();
        foreach (var part in csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (Guid.TryParse(part, out var id) && id != Guid.Empty && !ids.Contains(id))
                ids.Add(id);
        }
        return ids.Count == 0 ? null : ids;
    }

    /// <summary>
    /// Maps an entity to the wire DTO. PersonaPrompt is included for the owner
    /// only (so they can edit it); other tenant users of a shared agent get null.
    /// </summary>
    public static CustomAgentDto ToDto(CustomAgent agent, Guid callerUserId)
    {
        var isOwner = agent.OwnerUserId == callerUserId;
        return new CustomAgentDto
        {
            Id = agent.Id,
            Name = agent.Name,
            Description = agent.Description,
            Icon = agent.Icon,
            PersonaPrompt = isOwner ? agent.PersonaPrompt : null,
            AllowedTools = ParseToolsCsv(agent.AllowedToolsCsv)?.ToList(),
            KnowledgeDocumentIds = ParseDocumentIdsCsv(agent.KnowledgeDocumentIdsCsv)?.ToList() ?? new List<Guid>(),
            IsSharedWithTenant = agent.IsSharedWithTenant,
            IsOwner = isOwner,
            CreatedAt = agent.CreatedAtUtc
        };
    }
}
