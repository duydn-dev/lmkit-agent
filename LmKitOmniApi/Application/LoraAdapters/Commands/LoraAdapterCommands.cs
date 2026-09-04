using MediatR;

namespace LmKitOmniApi.Application.LoraAdapters.Commands;

/// <summary>
/// Registers a new adapter from an uploaded file. <see cref="Content"/> is the request's
/// live upload stream (consumed within the handler's request scope); TenantId is always
/// set by the controller from claims, never the body.
/// </summary>
public sealed class RegisterLoraAdapterCommand : IRequest<LoraAdapterMutationResult>
{
    public Guid TenantId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public Stream Content { get; set; } = Stream.Null;
    public long ContentLength { get; set; }
    public float? Scale { get; set; }
    public string? TargetModelId { get; set; }
}

/// <summary>Updates the mutable metadata (name / scale / active) of a registration. Null = unchanged.</summary>
public sealed class UpdateLoraAdapterCommand : IRequest<LoraAdapterMutationResult>
{
    public Guid TenantId { get; set; }
    public Guid Id { get; set; }
    public string? Name { get; set; }
    public float? Scale { get; set; }
    public bool? IsActive { get; set; }
}

/// <summary>Deletes a registration (row + file), tenant-scoped.</summary>
public sealed class DeleteLoraAdapterCommand : IRequest<LoraAdapterMutationResult>
{
    public Guid TenantId { get; set; }
    public Guid Id { get; set; }
}

/// <summary>
/// Binds/unbinds a LoRA adapter to a custom agent. The agent must be owned by the caller
/// inside the tenant; a non-null <see cref="LoraAdapterId"/> must be an existing tenant
/// registration; null clears the binding.
/// </summary>
public sealed class AssignLoraAdapterCommand : IRequest<LoraAssignResult>
{
    public Guid TenantId { get; set; }
    public Guid UserId { get; set; }
    public Guid AgentId { get; set; }
    public Guid? LoraAdapterId { get; set; }
}

/// <summary>How a mutation resolved; the controller maps this onto the HTTP contract.</summary>
public enum LoraMutationStatus
{
    Success,
    NotFound,
    ValidationFailed,
    FeatureDisabled
}

public sealed class LoraAdapterMutationResult
{
    public LoraMutationStatus Status { get; init; }
    public string? ErrorMessage { get; init; }
    public LoraAdapterDto? Adapter { get; init; }

    public static LoraAdapterMutationResult Success(LoraAdapterDto? adapter = null) =>
        new() { Status = LoraMutationStatus.Success, Adapter = adapter };
    public static LoraAdapterMutationResult NotFound() =>
        new() { Status = LoraMutationStatus.NotFound };
    public static LoraAdapterMutationResult ValidationFailed(string message) =>
        new() { Status = LoraMutationStatus.ValidationFailed, ErrorMessage = message };
    public static LoraAdapterMutationResult FeatureDisabled() =>
        new() { Status = LoraMutationStatus.FeatureDisabled };
}

/// <summary>How an assign resolved; distinguishes a missing agent (404) from a bad adapter id (400).</summary>
public enum LoraAssignStatus
{
    Success,
    FeatureDisabled,
    AgentNotFound,
    AdapterNotFound
}

public sealed class LoraAssignResult
{
    public LoraAssignStatus Status { get; init; }

    public static LoraAssignResult Success() => new() { Status = LoraAssignStatus.Success };
    public static LoraAssignResult FeatureDisabled() => new() { Status = LoraAssignStatus.FeatureDisabled };
    public static LoraAssignResult AgentNotFound() => new() { Status = LoraAssignStatus.AgentNotFound };
    public static LoraAssignResult AdapterNotFound() => new() { Status = LoraAssignStatus.AdapterNotFound };
}
