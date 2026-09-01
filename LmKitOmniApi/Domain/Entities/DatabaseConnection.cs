using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LmKitOmniApi.Domain.Entities;

/// <summary>
/// A tenant-registered connection to an EXTERNAL database the agent can introspect
/// and query READ-ONLY on the user's behalf. The connection string is a reversible
/// secret and is always stored encrypted (DataProtection, see
/// <c>DbConnectionSecretProtector</c>) — never returned to any client.
///
/// SAFETY: reads run under a read-only transaction and a deterministic statement
/// classifier; writes are NEVER auto-executed — they require an explicit HITL
/// approval and a pre-write backup (later phase). The strongest guarantee is that
/// the operator supplies a least-privilege, read-only DB account here.
/// </summary>
[Table("database_connections")]
public sealed class DatabaseConnection
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid TenantId { get; set; }

    /// <summary>Owner. Connections are managed by tenant admins but attributed to a user.</summary>
    public Guid UserId { get; set; }

    [MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    /// <summary>Engine: "Postgres" | "Sqlite" (more later). Parsed to the DbProvider enum.</summary>
    [MaxLength(32)]
    public string Provider { get; set; } = string.Empty;

    /// <summary>DataProtection-encrypted connection string (prefixed "dp:v1:"). Write-only from the API.</summary>
    public string ConnectionStringProtected { get; set; } = string.Empty;

    public bool IsActive { get; set; } = true;

    // ── Schema-index (Qdrant) tracking — mirrors Document's vectorization columns ──
    public bool IsIndexed { get; set; }

    [MaxLength(32)]
    public string IndexStatus { get; set; } = "Pending";

    public int IndexAttempts { get; set; }

    public DateTime? IndexLeaseUntilUtc { get; set; }

    [MaxLength(1000)]
    public string? LastIndexError { get; set; }

    public DateTime? LastIndexedAtUtc { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    public Tenant? Tenant { get; set; }
}
