using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LmKitOmniApi.Domain.Entities;

/// <summary>
/// A user-authored agent persona (Gems/GPTs style): a system-prompt persona,
/// an optional tool whitelist and optional pinned knowledge documents.
/// Owned by a user inside a tenant; optionally visible to the whole tenant.
/// </summary>
[Table("custom_agents")]
public sealed class CustomAgent
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid TenantId { get; set; }

    public Guid OwnerUserId { get; set; }

    [MaxLength(80)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(300)]
    public string? Description { get; set; }

    /// <summary>Emoji or icon key shown in pickers.</summary>
    [MaxLength(16)]
    public string? Icon { get; set; }

    /// <summary>Persona injected into the system prompt when chatting with this agent.</summary>
    public string PersonaPrompt { get; set; } = string.Empty;

    /// <summary>
    /// Comma-separated tool names the agent may use. Null/empty = the caller
    /// role's default tool set. Non-empty = intersection with the role set.
    /// </summary>
    [MaxLength(1000)]
    public string? AllowedToolsCsv { get; set; }

    /// <summary>
    /// Comma-separated document ids (owner's documents) that ground this agent.
    /// When present, RAG retrieval for the agent is restricted to these documents.
    /// </summary>
    [MaxLength(2000)]
    public string? KnowledgeDocumentIdsCsv { get; set; }

    /// <summary>When true every user in the tenant can chat with this agent.</summary>
    public bool IsSharedWithTenant { get; set; }

    /// <summary>
    /// Optional reference to a <see cref="LoraAdapterRegistration"/> (LoRA hot-swap).
    /// When set — and the LoRA feature is enabled and the registration is active with a
    /// present file — the chat orchestrator applies that adapter to the shared chat model
    /// for the duration of this agent's inference and removes it immediately afterwards.
    /// A SOFT reference (no enforced FK): a deleted/missing/inactive registration simply
    /// makes the request run without an adapter.
    /// </summary>
    public Guid? LoraAdapterId { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    public Tenant? Tenant { get; set; }
    public User? OwnerUser { get; set; }
}
