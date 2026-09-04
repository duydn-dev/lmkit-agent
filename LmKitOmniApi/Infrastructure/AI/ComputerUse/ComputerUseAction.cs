using System.Text.Json;

namespace LmKitOmniApi.Infrastructure.AI.ComputerUse;

/// <summary>The nine actions the computer-use agent understands.</summary>
public enum ComputerUseActionType
{
    /// <summary>Load a URL (side-effecting egress; allowlist + SSRF gated).</summary>
    Navigate,
    /// <summary>Click an element by ref, or by x/y coordinates (side-effecting).</summary>
    Click,
    /// <summary>Type text into an element by ref (side-effecting).</summary>
    Type,
    /// <summary>Press a key / chord, e.g. "Enter", "Ctrl+A" (side-effecting).</summary>
    Key,
    /// <summary>Scroll the viewport (read-only).</summary>
    Scroll,
    /// <summary>Idle for a number of milliseconds (read-only).</summary>
    Wait,
    /// <summary>Re-capture the current screen + element list without acting (read-only).</summary>
    Screenshot,
    /// <summary>The task is complete; carries a natural-language summary (terminal).</summary>
    Done,
    /// <summary>Hand control back to the human with a question (terminal).</summary>
    Ask,
}

/// <summary>
/// A single structured action the model chose for the next step, parsed from its JSON
/// output by <see cref="ComputerUseActionParser"/>. Fields are nullable and only the
/// ones relevant to <see cref="Type"/> are populated. Prefer <see cref="Ref"/>
/// (an accessibility element index from the observation) over raw <see cref="X"/>/
/// <see cref="Y"/> coordinates.
/// </summary>
public sealed record ComputerUseAction
{
    public required ComputerUseActionType Type { get; init; }

    // navigate
    public string? Url { get; init; }

    // click / type — element ref (preferred) or coordinate fallback
    public int? Ref { get; init; }
    public int? X { get; init; }
    public int? Y { get; init; }

    // type
    public string? Text { get; init; }

    // key
    public string? Keys { get; init; }

    // scroll
    public string? Direction { get; init; }
    public int? Amount { get; init; }

    // wait
    public int? Ms { get; init; }

    // done / ask
    public string? Summary { get; init; }
    public string? Question { get; init; }

    /// <summary>
    /// True for actions that change the world (navigate / click / type / key). These are
    /// the ones the loop gates on human approval when
    /// <see cref="ComputerUseOptions.RequireApprovalPerAction"/> is set. Read-only actions
    /// (screenshot / scroll / wait / done / ask) return false.
    /// </summary>
    public bool IsSideEffecting => Type is ComputerUseActionType.Navigate
        or ComputerUseActionType.Click
        or ComputerUseActionType.Type
        or ComputerUseActionType.Key;

    /// <summary>True for actions that end the loop (done / ask).</summary>
    public bool IsTerminal => Type is ComputerUseActionType.Done or ComputerUseActionType.Ask;

    /// <summary>A short, human-readable one-liner for approval prompts / audit / step markers.</summary>
    public string Describe() => Type switch
    {
        ComputerUseActionType.Navigate => $"navigate → {Url}",
        ComputerUseActionType.Click => Ref is int r ? $"click element #{r}" : $"click ({X},{Y})",
        ComputerUseActionType.Type => $"type into element #{Ref}: \"{Truncate(Text, 60)}\"",
        ComputerUseActionType.Key => $"press keys: {Keys}",
        ComputerUseActionType.Scroll => $"scroll {Direction} {Amount}",
        ComputerUseActionType.Wait => $"wait {Ms}ms",
        ComputerUseActionType.Screenshot => "re-observe (screenshot)",
        ComputerUseActionType.Done => $"done: {Truncate(Summary, 80)}",
        ComputerUseActionType.Ask => $"ask: {Truncate(Question, 80)}",
        _ => Type.ToString(),
    };

    private static string Truncate(string? s, int max)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        return s.Length <= max ? s : s[..max] + "…";
    }
}
