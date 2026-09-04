namespace LmKitOmniApi.Infrastructure.AI.ComputerUse;

/// <summary>
/// One computer-use run request. <see cref="SessionId"/> is a fresh id the controller
/// mints so the client (and the approval records) can correlate the run; <see cref="Role"/>
/// is the caller's role (Admin/User) from the JWT. <see cref="StartUrl"/> is optional —
/// when present the loop opens it first (the user's own input, so it is allowlist+SSRF
/// gated but not approval-gated).
/// </summary>
public sealed record ComputerUseRequest(
    Guid TenantId,
    Guid UserId,
    Guid SessionId,
    string Role,
    string TaskGoal,
    string StartUrl);

/// <summary>
/// The interactive perception→action loop: observe (screenshot + numbered elements) →
/// decide ONE action → (approval for side-effecting actions) → act → observe → … until
/// <c>done</c>/<c>ask</c>, the step cap, or the per-session wall-clock cap. Streams the
/// same SSE marker channel as chat: <c>[THINKING]</c> progress, <c>[STEP:{…}]</c> per
/// step, <c>[FILE:{…}]</c> per screenshot, and <c>[HITL_APPROVAL_REQUIRED:{id}]</c> before
/// each gated action. Enforces the navigation allowlist, the step cap, the wall-clock cap,
/// and the credential/CAPTCHA refusal (hand off to a human) on every iteration.
/// </summary>
public interface IComputerUseAgent
{
    /// <summary>True only when the executor is enabled (options on + image configured).</summary>
    bool IsEnabled { get; }

    /// <summary>Drives the loop and yields SSE marker strings until the run terminates.</summary>
    IAsyncEnumerable<string> RunAsync(ComputerUseRequest request, CancellationToken ct = default);
}
