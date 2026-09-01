namespace LmKitOmniApi.Application.ApiKeys;

/// <summary>
/// Validation limits for self-service API keys. The raw-key prefix is intentionally
/// NOT stored or returned: only the SHA-256 hash is persisted, so a prefix cannot be
/// derived after creation — keys are identified by <c>Name</c> instead.
/// </summary>
public static class ApiKeyRules
{
    public const int MaxNameLength = 64;
    public const int MinExpiresInDays = 1;
    public const int MaxExpiresInDays = 365;
    public const int DefaultExpiresInDays = 90;
    /// <summary>0 = unlimited.</summary>
    public const int MaxRequestBudget = 1_000_000;
    public const int MaxActiveKeysPerUser = 5;
}

/// <summary>Listing shape — never contains any raw-key material.</summary>
public sealed class ApiKeyDto
{
    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public int MaxRequests { get; init; }
    public int UsedRequests { get; init; }
    public DateTime ExpiresAtUtc { get; init; }
    public DateTime CreatedAtUtc { get; init; }
    /// <summary>True when the key is neither revoked nor expired.</summary>
    public bool IsActive { get; init; }
}

/// <summary>Creation response — the ONLY place the raw key ever appears.</summary>
public sealed class CreatedApiKeyDto
{
    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string RawKey { get; init; } = string.Empty;
    public DateTime ExpiresAtUtc { get; init; }
}

public enum ApiKeyMutationStatus
{
    Success,
    ValidationFailed
}

public sealed class CreateApiKeyResult
{
    public ApiKeyMutationStatus Status { get; init; }
    public string? ErrorMessage { get; init; }
    public CreatedApiKeyDto? Key { get; init; }
}
