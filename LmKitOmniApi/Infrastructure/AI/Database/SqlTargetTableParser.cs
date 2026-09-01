using System.Text.RegularExpressions;

namespace LmKitOmniApi.Infrastructure.AI.Database;

/// <summary>
/// Extracts the single table a write statement modifies, so it can be backed up
/// before the write. Conservative: only a PLAIN identifier (optionally
/// schema-qualified) is accepted — quoted/bracketed/unusual targets, CTEs, or
/// multi-target statements return null. A null target makes the write path REFUSE,
/// so "back up first" is never skipped by guessing, and the returned name is always
/// a safe identifier to interpolate into the backup DDL.
/// </summary>
public static class SqlTargetTableParser
{
    private static readonly RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant;
    // Plain identifier or schema.table — letters/digits/underscore/$ only.
    private const string Identifier = @"([A-Za-z_][\w$]*(?:\.[A-Za-z_][\w$]*)?)";

    private static readonly Regex[] Patterns =
    {
        new($@"^\s*UPDATE\s+(?:ONLY\s+){{0,1}}{Identifier}(?:\s|$)", Options),
        new($@"^\s*DELETE\s+FROM\s+(?:ONLY\s+){{0,1}}{Identifier}(?:\s|$)", Options),
        new($@"^\s*INSERT\s+INTO\s+{Identifier}(?:\s|\(|$)", Options),
        new($@"^\s*REPLACE\s+INTO\s+{Identifier}(?:\s|\(|$)", Options),
        new($@"^\s*MERGE\s+INTO\s+{Identifier}(?:\s|$)", Options),
    };

    /// <summary>Returns the target table (a safe plain/qualified identifier), or null if it cannot be determined.</summary>
    public static string? TryGetTargetTable(string? sql)
    {
        if (string.IsNullOrWhiteSpace(sql)) return null;
        foreach (var pattern in Patterns)
        {
            var match = pattern.Match(sql);
            if (match.Success) return match.Groups[1].Value;
        }
        return null;
    }
}
