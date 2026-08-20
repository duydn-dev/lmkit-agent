namespace LmKitOmniApi.Infrastructure.AI;

/// <summary>
/// Central retention rules for persistent agent memory. Profile and preference
/// data remains until explicitly deleted; transient memories expire by class.
/// </summary>
public static class MemoryRetentionPolicy
{
    public static DateTime? GetDefaultExpiration(string memoryType, DateTime utcNow) =>
        memoryType.Trim().ToLowerInvariant() switch
        {
            "session" or "shortterm" or "short-term" => utcNow.AddHours(24),
            "episodic" => utcNow.AddDays(90),
            "fact" => utcNow.AddDays(180),
            "userprofile" or "preference" or "semantic" => null,
            _ => utcNow.AddDays(90),
        };
}
