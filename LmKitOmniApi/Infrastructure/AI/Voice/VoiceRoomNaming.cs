namespace LmKitOmniApi.Infrastructure.AI.Voice;

/// <summary>
/// THE authoritative LiveKit room-naming function.
///
/// Every party that needs a room name — the browser token endpoint
/// (<c>SpeechController.GetLiveKitToken</c>) and the server-side agent
/// (<see cref="VoiceRoomAgentHostedService"/>) — MUST derive it here. Previously the
/// endpoint minted <c>{tenant}-{room}</c> while the hosted agent joined the bare
/// <c>Voice:Room</c> value, so the agent and its callers were always in two different
/// rooms and the agent could never hear anyone.
///
/// SCOPE: a room is scoped to <b>tenant + user</b>, never to a tenant alone. Two
/// colleagues inside one tenant asking for the same label land in DIFFERENT rooms, so
/// they can never hear each other's audio. The label is only a suffix chosen by the
/// caller (default <see cref="DefaultLabel"/>); it can never widen the scope because the
/// tenant/user prefix is server-supplied and the label is sanitized to
/// <c>[A-Za-z0-9_-]</c>, which cannot introduce another separator segment that collides
/// with a different identity's prefix.
///
/// A caller who wants several concurrent rooms (e.g. one per browser tab) passes a
/// distinct label — the naming stays per-user, which is the isolation boundary that
/// matters. <see cref="VoiceRoomAgentHostedService"/> documents how the single-room hosted
/// agent is pointed at one of these rooms.
/// </summary>
public static class VoiceRoomNaming
{
    /// <summary>Default room label used when a caller does not ask for a specific one.</summary>
    public const string DefaultLabel = "omni-room";

    /// <summary>Longest raw label accepted from a caller before sanitization.</summary>
    public const int MaxLabelInputLength = 100;

    /// <summary>Longest sanitized label kept in the final room name (keeps room names bounded).</summary>
    public const int MaxSanitizedLabelLength = 64;

    /// <summary>
    /// Normalizes a caller-supplied room label to <c>[A-Za-z0-9_-]</c>, collapsing every other
    /// run of characters to a single '-' and trimming leading/trailing separators. Returns
    /// false with a reason when the label is too long or contains nothing usable.
    /// </summary>
    public static bool TrySanitizeLabel(string? label, out string safeLabel, out string? error)
    {
        safeLabel = string.Empty;
        error = null;

        var raw = string.IsNullOrWhiteSpace(label) ? DefaultLabel : label.Trim();
        if (raw.Length > MaxLabelInputLength)
        {
            error = $"Room must contain between 1 and {MaxLabelInputLength} characters.";
            return false;
        }

        var sanitized = System.Text.RegularExpressions.Regex
            .Replace(raw, "[^a-zA-Z0-9_-]+", "-")
            .Trim('-');
        if (sanitized.Length == 0)
        {
            error = "Room contains no supported characters.";
            return false;
        }

        safeLabel = sanitized.Length > MaxSanitizedLabelLength
            ? sanitized[..MaxSanitizedLabelLength].TrimEnd('-')
            : sanitized;
        if (safeLabel.Length == 0)
        {
            error = "Room contains no supported characters.";
            return false;
        }
        return true;
    }

    /// <summary>
    /// Builds the scoped room name for an ALREADY-sanitized label. Prefer
    /// <see cref="TryScopedRoom"/> — this overload exists for callers that sanitized once
    /// and want the exact same string again.
    /// </summary>
    public static string ScopedRoom(Guid tenantId, Guid userId, string sanitizedLabel) =>
        $"{tenantId:N}-{userId:N}-{sanitizedLabel}";

    /// <summary>
    /// Number of characters a <c>Guid:N</c> prefix occupies in a room name.
    /// </summary>
    private const int GuidHexLength = 32;

    /// <summary>
    /// The inverse of <see cref="ScopedRoom"/>: reads the tenant and user a room name belongs
    /// to. Used by the multi-room dispatcher to prove — before joining anything — that a room it
    /// was asked to serve really is the room of the identity that asked for it. Returns false
    /// for any name that is not exactly <c>{tenant:N}-{user:N}-{label}</c>.
    /// </summary>
    public static bool TryParseScopedRoom(string? room, out Guid tenantId, out Guid userId, out string label)
    {
        tenantId = Guid.Empty;
        userId = Guid.Empty;
        label = string.Empty;

        if (string.IsNullOrEmpty(room)) return false;
        // 32 + '-' + 32 + '-' + at least one label character.
        if (room.Length < (GuidHexLength * 2) + 3) return false;
        if (room[GuidHexLength] != '-' || room[(GuidHexLength * 2) + 1] != '-') return false;

        if (!Guid.TryParseExact(room[..GuidHexLength], "N", out tenantId) || tenantId == Guid.Empty)
            return false;
        if (!Guid.TryParseExact(room.Substring(GuidHexLength + 1, GuidHexLength), "N", out userId) || userId == Guid.Empty)
        {
            tenantId = Guid.Empty;
            return false;
        }

        label = room[((GuidHexLength * 2) + 2)..];
        if (label.Length == 0 || !TrySanitizeLabel(label, out var sanitized, out _) || !string.Equals(sanitized, label, StringComparison.Ordinal))
        {
            tenantId = Guid.Empty;
            userId = Guid.Empty;
            label = string.Empty;
            return false;
        }
        return true;
    }

    /// <summary>
    /// The one call both the token endpoint and the agent use: validate the identity,
    /// sanitize the label, and produce the tenant+user scoped room name. Returns false with
    /// a human-readable reason when the identity is missing or the label is unusable.
    /// </summary>
    public static bool TryScopedRoom(Guid tenantId, Guid userId, string? label, out string room, out string? error)
    {
        room = string.Empty;

        if (tenantId == Guid.Empty)
        {
            error = "A non-empty tenant id is required to scope a voice room.";
            return false;
        }
        if (userId == Guid.Empty)
        {
            error = "A non-empty user id is required to scope a voice room.";
            return false;
        }
        if (!TrySanitizeLabel(label, out var safeLabel, out error))
            return false;

        room = ScopedRoom(tenantId, userId, safeLabel);
        error = null;
        return true;
    }
}
