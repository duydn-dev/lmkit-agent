using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LmKitOmniApi.Infrastructure.AI.Voice;

/// <summary>
/// A user's explicit, time-boxed consent for the server-side voice agent to join ONE of their
/// rooms. This is the ONLY thing that can put an agent participant into a call: the dispatcher
/// never joins a room merely because LiveKit says it exists.
///
/// A grant is minted by <c>GET /api/speech/token?agent=true</c> from the AUTHENTICATED caller's
/// identity, so <see cref="Room"/> is always derived server-side through
/// <see cref="VoiceRoomNaming"/> and can never be chosen by the caller.
/// </summary>
public sealed record VoiceAgentGrant
{
    /// <summary>Full scoped room name — always <c>{tenant:N}-{user:N}-{label}</c>.</summary>
    public required string Room { get; init; }

    /// <summary>Tenant that owns the room. The session's whole context is built from this.</summary>
    public required Guid TenantId { get; init; }

    /// <summary>User who asked for the agent.</summary>
    public required Guid UserId { get; init; }

    /// <summary>Sanitized room label (the suffix of <see cref="Room"/>).</summary>
    public string Label { get; init; } = VoiceRoomNaming.DefaultLabel;

    /// <summary>Voice/preset the reply is spoken with.</summary>
    public string Voice { get; init; } = "default";

    public DateTimeOffset GrantedAtUtc { get; init; }

    /// <summary>
    /// Hard expiry. A grant is a lease, not a standing permission: when it lapses the
    /// dispatcher cancels the session and the agent leaves the room.
    /// </summary>
    public DateTimeOffset ExpiresAtUtc { get; init; }

    public bool IsActiveAt(DateTimeOffset nowUtc) => nowUtc < ExpiresAtUtc;

    /// <summary>
    /// Defence in depth: the room name must be exactly the one this tenant+user+label produces.
    /// Nothing should be able to hand the dispatcher a grant whose room belongs to somebody
    /// else — the registry derives the name rather than accepting one — but the dispatcher
    /// re-checks before joining, because "join the room this record names" is the single place
    /// a bug could put an agent into another tenant's call.
    /// </summary>
    public bool IsSelfConsistent() =>
        VoiceRoomNaming.TryScopedRoom(TenantId, UserId, Label, out var expected, out _)
        && string.Equals(expected, Room, StringComparison.Ordinal);
}

/// <summary>
/// The consent ledger the room dispatcher reads. In-memory and bounded on every axis: total
/// grants, grants per user, and a TTL on each one.
/// </summary>
public interface IVoiceAgentConsentRegistry
{
    /// <summary>
    /// Records (or renews) consent for the caller's own room. The room name is DERIVED from
    /// <paramref name="tenantId"/>/<paramref name="userId"/>/<paramref name="roomLabel"/>, so a
    /// caller can never grant an agent access to a room that is not theirs.
    /// Returns false with a reason when the identity/label is unusable or a cap is reached;
    /// a refusal is never fatal to the caller's own token.
    /// </summary>
    bool TryGrant(
        Guid tenantId,
        Guid userId,
        string? roomLabel,
        string? voice,
        DateTimeOffset nowUtc,
        out VoiceAgentGrant? grant,
        out string? error);

    /// <summary>Withdraws consent for the caller's own room. Returns true when a grant was removed.</summary>
    bool Revoke(Guid tenantId, Guid userId, string? roomLabel);

    /// <summary>Every grant still live at <paramref name="nowUtc"/>, oldest first. Expired entries are purged.</summary>
    IReadOnlyList<VoiceAgentGrant> ActiveGrants(DateTimeOffset nowUtc);

    /// <summary>Number of tracked grants (live or not yet purged) — for diagnostics/tests.</summary>
    int Count { get; }
}

/// <summary>
/// In-memory <see cref="IVoiceAgentConsentRegistry"/>. Process-local by design: the grant is
/// created by the very request that mints the caller's LiveKit token, so the replica that will
/// dispatch an agent is the replica the caller already talked to. A multi-replica deployment
/// therefore serves each room from exactly one replica (no duplicate agents), at the cost of
/// not being able to hand a room to a less-busy replica — see T2-ROUND4.md.
///
/// BOUNDED: an authenticated caller can otherwise grow this dictionary without limit by asking
/// for a token with a fresh room label each time. Per-user and total caps make it O(1) memory.
/// </summary>
public sealed class VoiceAgentConsentRegistry : IVoiceAgentConsentRegistry
{
    private readonly VoiceOptions _options;
    private readonly ILogger<VoiceAgentConsentRegistry> _logger;
    private readonly object _lock = new();
    private readonly Dictionary<string, VoiceAgentGrant> _grants = new(StringComparer.Ordinal);

    public VoiceAgentConsentRegistry(IOptions<VoiceOptions> options, ILogger<VoiceAgentConsentRegistry> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public int Count
    {
        get { lock (_lock) return _grants.Count; }
    }

    public bool TryGrant(
        Guid tenantId,
        Guid userId,
        string? roomLabel,
        string? voice,
        DateTimeOffset nowUtc,
        out VoiceAgentGrant? grant,
        out string? error)
    {
        grant = null;

        // The room name is server-derived, never caller-supplied.
        if (!VoiceRoomNaming.TrySanitizeLabel(roomLabel, out var label, out error))
            return false;
        if (!VoiceRoomNaming.TryScopedRoom(tenantId, userId, label, out var room, out error))
            return false;

        var ttl = TimeSpan.FromMinutes(Math.Max(1, _options.AgentConsentTtlMinutes));
        var candidate = new VoiceAgentGrant
        {
            Room = room,
            TenantId = tenantId,
            UserId = userId,
            Label = label,
            Voice = string.IsNullOrWhiteSpace(voice) ? _options.DefaultVoice : voice.Trim(),
            GrantedAtUtc = nowUtc,
            ExpiresAtUtc = nowUtc + ttl
        };

        lock (_lock)
        {
            PurgeExpiredLocked(nowUtc);

            if (!_grants.ContainsKey(room))
            {
                // Per-user cap first: a user with many tabs evicts their OWN oldest grant rather
                // than consuming the whole process budget.
                var mine = _grants.Values
                    .Where(existing => existing.TenantId == tenantId && existing.UserId == userId)
                    .OrderBy(existing => existing.ExpiresAtUtc)
                    .ToList();
                var perUserCap = Math.Max(1, _options.MaxAgentConsentGrantsPerUser);
                for (var i = 0; mine.Count - i >= perUserCap; i++)
                    _grants.Remove(mine[i].Room);

                var totalCap = Math.Max(1, _options.MaxAgentConsentGrants);
                if (_grants.Count >= totalCap)
                {
                    error = $"The voice agent consent registry is full ({totalCap} rooms); try again shortly.";
                    _logger.LogWarning(
                        "Refusing voice agent consent for tenant {TenantId}: registry is at its cap of {Cap}.",
                        tenantId, totalCap);
                    return false;
                }
            }

            _grants[room] = candidate;
        }

        grant = candidate;
        error = null;
        return true;
    }

    public bool Revoke(Guid tenantId, Guid userId, string? roomLabel)
    {
        if (!VoiceRoomNaming.TryScopedRoom(tenantId, userId, roomLabel, out var room, out _))
            return false;

        lock (_lock)
        {
            // Re-check ownership even though the name was derived: removal must never be able to
            // drop somebody else's grant.
            if (!_grants.TryGetValue(room, out var existing)) return false;
            if (existing.TenantId != tenantId || existing.UserId != userId) return false;
            return _grants.Remove(room);
        }
    }

    public IReadOnlyList<VoiceAgentGrant> ActiveGrants(DateTimeOffset nowUtc)
    {
        lock (_lock)
        {
            PurgeExpiredLocked(nowUtc);
            return _grants.Values
                .OrderBy(existing => existing.GrantedAtUtc)
                .ThenBy(existing => existing.Room, StringComparer.Ordinal)
                .ToList();
        }
    }

    private void PurgeExpiredLocked(DateTimeOffset nowUtc)
    {
        if (_grants.Count == 0) return;
        List<string>? dead = null;
        foreach (var (room, existing) in _grants)
        {
            if (existing.IsActiveAt(nowUtc)) continue;
            (dead ??= new List<string>()).Add(room);
        }
        if (dead is null) return;
        foreach (var room in dead) _grants.Remove(room);
    }
}
