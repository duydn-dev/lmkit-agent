using MediatR;

// LoraAdapterDto is declared in the enclosing LmKitOmniApi.Application.LoraAdapters
// namespace, so it is visible here (a nested namespace) without a using.
namespace LmKitOmniApi.Application.LoraAdapters.Queries;

/// <summary>Lists a tenant's LoRA adapter registrations (empty when the feature is off).</summary>
public sealed class GetLoraAdaptersQuery : IRequest<IReadOnlyList<LoraAdapterDto>>
{
    public Guid TenantId { get; set; }
}

/// <summary>Reads one registration by id, tenant-scoped. Null when missing or the feature is off.</summary>
public sealed class GetLoraAdapterQuery : IRequest<LoraAdapterDto?>
{
    public Guid TenantId { get; set; }
    public Guid Id { get; set; }
}
