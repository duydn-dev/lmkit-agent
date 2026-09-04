namespace LmKitOmniApi.Infrastructure.AI.ComputerUse;

/// <summary>
/// Configuration for the interactive COMPUTER-USE agent — the perception→action loop
/// that drives a real browser in a locked container (click / type / navigate against
/// an accessibility-grounded element list). Bound from the "ComputerUse" configuration
/// section.
///
/// DISABLED BY DEFAULT and the single most safety-sensitive surface in the product:
/// it is a networked, egress-capable browser the model can act inside. It only runs
/// when an operator explicitly enables it AND provisions a hardened interactive-browser
/// container image. When disabled, <see cref="IComputerUseExecutor.IsEnabled"/> /
/// <see cref="IComputerUseAgent.IsEnabled"/> report false, the controller returns 501,
/// and nothing can ever launch.
///
/// SAFETY MODEL (see <see cref="ComputerUseExecutor"/> / <see cref="ComputerUseAgent"/>):
///  1. Navigation is restricted to <see cref="AllowedHosts"/> — an EXPLICIT allowlist.
///     UNLIKE the read-only browse tool's <c>BrowserFetchOptions.AllowedHosts</c> (where
///     an empty list means "any public host"), here an EMPTY list means NOTHING is
///     allowed. A tighter default for a far more powerful tool: an operator must
///     deliberately name every host the agent may visit.
///  2. Every navigation is additionally vetted by
///     <c>ToolSandboxService.ValidateUrlAsync</c> on every hop, so internal / loopback /
///     link-local / cloud-metadata targets are always blocked regardless of the allowlist.
///  3. Every SIDE-EFFECTING action (navigate / click / type / key) requires human
///     approval when <see cref="RequireApprovalPerAction"/> is true (the default);
///     read-only observation (screenshot / scroll / wait / done / ask) never does.
///  4. The container is non-root, cap-drop ALL, no-new-privileges, read-only rootfs,
///     with memory / cpu / pids / wall-clock caps.
///  5. A step cap (<see cref="MaxSteps"/>) and a per-session wall-clock cap
///     (<see cref="SessionWallClockSeconds"/>) bound every run.
/// </summary>
public sealed class ComputerUseOptions
{
    public const string SectionName = "ComputerUse";

    /// <summary>Master switch. False (default) = the computer-use agent is off everywhere.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Container image that runs ONE interactive step: it receives the requested action
    /// (as a JSON <c>--action</c> argument), applies it against the persistent browser
    /// profile mounted at <c>/session</c>, and writes a single-line observation JSON to
    /// stdout (url, title, numbered interactive elements, and a screenshot path under
    /// <c>/session</c>). No image configured ⇒ the tool is treated as disabled.
    /// </summary>
    public string Image { get; set; } = string.Empty;

    /// <summary>Container runtime executable (default "docker").</summary>
    public string RuntimePath { get; set; } = "docker";

    /// <summary>
    /// EXPLICIT navigation allowlist of permitted destination hostnames. EMPTY = NOTHING
    /// allowed (deny-all) — the deliberately strict default for this high-power tool.
    /// A navigate whose host is not listed is refused before any container launches,
    /// and internal/loopback/metadata targets are always blocked by the SSRF gate on
    /// top of this. Exact, case-insensitive host match (mirrors DbEgressValidator).
    /// </summary>
    public List<string> AllowedHosts { get; set; } = new();

    /// <summary>
    /// Optional operator-provisioned container network name. When set, each step's
    /// container is launched with <c>--network &lt;name&gt;</c> — the recommended place to
    /// enforce real per-host egress restriction (firewall/proxy) that <c>docker run</c>
    /// flags alone cannot express. When empty the runtime default network is used and the
    /// <see cref="AllowedHosts"/> pre-navigation check is the only host constraint (which
    /// covers only the initial target, not in-page redirects/subresources — see remarks
    /// on <see cref="ComputerUseExecutor"/>).
    /// </summary>
    public string NetworkName { get; set; } = string.Empty;

    /// <summary>Maximum number of model-decided steps per run (perception→action iterations).</summary>
    public int MaxSteps { get; set; } = 15;

    /// <summary>Hard wall-clock limit for a SINGLE step's container (seconds).</summary>
    public int StepTimeoutSeconds { get; set; } = 30;

    /// <summary>Hard wall-clock limit for the WHOLE session across all steps (seconds).</summary>
    public int SessionWallClockSeconds { get; set; } = 300;

    /// <summary>Memory ceiling passed to the runtime per step (MB).</summary>
    public int MemoryMb { get; set; } = 512;

    /// <summary>CPU quota passed to the runtime per step (e.g. 1.0 = one core).</summary>
    public double Cpus { get; set; } = 1.0;

    /// <summary>Fork-bomb bound passed to the runtime (a browser spawns many helpers).</summary>
    public int PidsLimit { get; set; } = 512;

    /// <summary>Max bytes of a persisted per-step screenshot; a larger capture is dropped (file id stays null).</summary>
    public long MaxScreenshotBytes { get; set; } = 5 * 1024 * 1024;

    /// <summary>Max interactive elements surfaced from one observation (caps the model context + argument size).</summary>
    public int MaxElements { get; set; } = 100;

    /// <summary>
    /// When true (the default), every side-effecting action (navigate / click / type /
    /// key) is gated on human approval via <see cref="IComputerUseApprovalGate"/> before
    /// it executes. Read-only actions (screenshot / scroll / wait / done / ask) always
    /// bypass approval. Turning this off is strongly discouraged and must be a conscious
    /// operator choice.
    /// </summary>
    public bool RequireApprovalPerAction { get; set; } = true;

    /// <summary>How long the default approval gate waits for a human decision before failing closed (seconds).</summary>
    public int ApprovalTimeoutSeconds { get; set; } = 300;
}
