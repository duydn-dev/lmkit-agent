using LmKitOmniApi.Infrastructure.AI.Security;

namespace LmKitOmniApi.Infrastructure.AI.Research;

/// <summary>
/// Narrow seam over the SSRF gate so <see cref="ResearchContentFetcher"/> can be
/// unit-tested without DNS/network. Production traffic always flows through
/// <see cref="SandboxResearchUrlValidator"/> →
/// <see cref="ToolSandboxService.ValidateUrlAsync"/> (scheme allow-list,
/// private/loopback/metadata host blocking, and per-address DNS re-vetting).
/// </summary>
public interface IResearchUrlValidator
{
    Task<PathValidationResult> ValidateAsync(string url, CancellationToken ct = default);
}

/// <summary>
/// Production implementation: delegates straight to the shared
/// <see cref="ToolSandboxService"/> SSRF gate (never re-implements the rules).
/// </summary>
public sealed class SandboxResearchUrlValidator : IResearchUrlValidator
{
    private readonly ToolSandboxService _sandbox;

    public SandboxResearchUrlValidator(ToolSandboxService sandbox) => _sandbox = sandbox;

    public Task<PathValidationResult> ValidateAsync(string url, CancellationToken ct = default)
        => _sandbox.ValidateUrlAsync(url, ct);
}
