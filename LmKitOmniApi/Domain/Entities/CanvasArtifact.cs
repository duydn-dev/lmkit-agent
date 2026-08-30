using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LmKitOmniApi.Domain.Entities;

/// <summary>
/// An editable workspace document (canvas). Versions are append-only rows
/// sharing one <see cref="RootId"/>; the latest version is the max
/// <see cref="Version"/> for that root. Owned by a user inside a tenant and
/// optionally attached to a chat session.
/// </summary>
[Table("canvas_artifacts")]
public sealed class CanvasArtifact
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Stable identity shared by all versions of one artifact.</summary>
    public Guid RootId { get; set; }

    public Guid TenantId { get; set; }

    public Guid UserId { get; set; }

    public Guid? ChatSessionId { get; set; }

    [MaxLength(120)]
    public string Title { get; set; } = string.Empty;

    /// <summary>"markdown" | "code" | "text".</summary>
    [MaxLength(30)]
    public string Kind { get; set; } = "markdown";

    /// <summary>Language hint for code artifacts (e.g. "csharp").</summary>
    [MaxLength(40)]
    public string? Language { get; set; }

    public string Content { get; set; } = string.Empty;

    /// <summary>1-based, increments per saved version of the same root.</summary>
    public int Version { get; set; } = 1;

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public Tenant? Tenant { get; set; }
    public User? User { get; set; }
    public ChatSession? ChatSession { get; set; }
}
