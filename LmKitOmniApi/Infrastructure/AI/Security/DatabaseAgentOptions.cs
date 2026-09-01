namespace LmKitOmniApi.Infrastructure.AI.Security;

/// <summary>
/// Configuration for the external-database agent. Bound from "DatabaseAgent".
/// DISABLED BY DEFAULT: connecting to external databases with stored credentials
/// is high-risk, so the query tool is only offered to the agent when an operator
/// explicitly enables it. Connection management (admin CRUD) is available
/// independently so admins can prepare connections before the tool is turned on.
/// </summary>
public sealed class DatabaseAgentOptions
{
    public const string SectionName = "DatabaseAgent";

    /// <summary>Master switch for the agent's db_query tool. False (default) = off.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Optional egress allowlist of permitted database hostnames. When non-empty,
    /// a connection whose host is not listed is refused. Internal/loopback/link-local
    /// addresses are ALWAYS blocked regardless of this list (SSRF guard).
    /// </summary>
    public List<string> AllowedHosts { get; set; } = new();

    /// <summary>Max rows returned by a single read-only query (hard cap).</summary>
    public int MaxRows { get; set; } = 500;

    /// <summary>Per-query command timeout (seconds).</summary>
    public int QueryTimeoutSeconds { get; set; } = 20;
}
