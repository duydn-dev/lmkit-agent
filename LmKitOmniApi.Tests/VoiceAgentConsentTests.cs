using LmKitOmniApi.Infrastructure.AI.Voice;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LmKitOmniApi.Tests;

/// <summary>
/// CONSENT is the gate on the multi-room voice agent: a room existing is not permission to put
/// a listener in it. These pin the properties that make the ledger safe to hand to a dispatcher —
/// the room name is derived from the authenticated identity (never from the caller), a grant is
/// a lease that lapses, it can be withdrawn, and an authenticated caller cannot grow it without
/// bound.
/// </summary>
public sealed class VoiceAgentConsentRegistryTests
{
    private static readonly Guid TenantA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid TenantB = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");
    private static readonly Guid UserA1 = Guid.Parse("11111111-0000-0000-0000-000000000001");
    private static readonly Guid UserA2 = Guid.Parse("11111111-0000-0000-0000-000000000002");
    private static readonly DateTimeOffset Now = new(2026, 9, 7, 12, 0, 0, TimeSpan.Zero);

    private static VoiceAgentConsentRegistry Registry(Action<VoiceOptions>? configure = null)
    {
        var options = new VoiceOptions();
        configure?.Invoke(options);
        return new VoiceAgentConsentRegistry(
            Options.Create(options), NullLogger<VoiceAgentConsentRegistry>.Instance);
    }

    [Fact]
    public void Grant_DerivesTheRoomFromTheAuthenticatedIdentity_NotFromTheCaller()
    {
        var registry = Registry();

        // The caller supplies a LABEL only. Even a label shaped like somebody else's full room
        // name is sanitized and then prefixed with the caller's own tenant+user.
        var hostile = $"{TenantB:N}-{UserA2:N}-omni-room";
        Assert.True(registry.TryGrant(TenantA, UserA1, hostile, null, Now, out var grant, out var error));
        Assert.Null(error);
        Assert.NotNull(grant);

        Assert.True(VoiceRoomNaming.TryParseScopedRoom(grant!.Room, out var tenantId, out var userId, out _));
        Assert.Equal(TenantA, tenantId);
        Assert.Equal(UserA1, userId);
        Assert.StartsWith($"{TenantA:N}-{UserA1:N}-", grant.Room, StringComparison.Ordinal);
        Assert.True(grant.IsSelfConsistent());
    }

    [Fact]
    public void Grant_MatchesTheRoomTheTokenEndpointMints()
    {
        var registry = Registry();
        Assert.True(registry.TryGrant(TenantA, UserA1, "omni-room", null, Now, out var grant, out _));
        Assert.True(VoiceRoomNaming.TryScopedRoom(TenantA, UserA1, "omni-room", out var callerRoom, out _));

        // If these ever diverge the agent joins a room the caller is not in.
        Assert.Equal(callerRoom, grant!.Room);
    }

    [Fact]
    public void Grant_IsALease_AndLapses()
    {
        var registry = Registry(options => options.AgentConsentTtlMinutes = 10);
        Assert.True(registry.TryGrant(TenantA, UserA1, "omni-room", null, Now, out var grant, out _));

        Assert.Equal(Now.AddMinutes(10), grant!.ExpiresAtUtc);
        Assert.Single(registry.ActiveGrants(Now.AddMinutes(9)));
        Assert.Empty(registry.ActiveGrants(Now.AddMinutes(10)));
        Assert.Equal(0, registry.Count); // the expired entry is purged, not just hidden
    }

    [Fact]
    public void Grant_ForTheSameRoom_Renews_RatherThanAccumulating()
    {
        var registry = Registry(options => options.AgentConsentTtlMinutes = 10);

        Assert.True(registry.TryGrant(TenantA, UserA1, "omni-room", null, Now, out _, out _));
        Assert.True(registry.TryGrant(TenantA, UserA1, "omni-room", null, Now.AddMinutes(5), out var renewed, out _));

        Assert.Equal(1, registry.Count);
        Assert.Equal(Now.AddMinutes(15), renewed!.ExpiresAtUtc);
    }

    [Fact]
    public void Revoke_RemovesOnlyTheCallersOwnGrant()
    {
        var registry = Registry();
        Assert.True(registry.TryGrant(TenantA, UserA1, "omni-room", null, Now, out var mine, out _));
        Assert.True(registry.TryGrant(TenantA, UserA2, "omni-room", null, Now, out var theirs, out _));

        Assert.True(registry.Revoke(TenantA, UserA1, "omni-room"));

        var remaining = Assert.Single(registry.ActiveGrants(Now));
        Assert.Equal(theirs!.Room, remaining.Room);
        Assert.NotEqual(mine!.Room, remaining.Room);

        // Revoking again is a no-op, not an error, and never touches anybody else.
        Assert.False(registry.Revoke(TenantA, UserA1, "omni-room"));
        Assert.Single(registry.ActiveGrants(Now));
    }

    [Fact]
    public void OneUser_CannotGrowTheLedgerWithoutBound()
    {
        var registry = Registry(options => options.MaxAgentConsentGrantsPerUser = 3);

        for (var i = 0; i < 12; i++)
            Assert.True(registry.TryGrant(TenantA, UserA1, $"tab-{i}", null, Now.AddSeconds(i), out _, out _));

        Assert.Equal(3, registry.Count);
        // The survivors are the most recent ones: the oldest lease is what gets dropped.
        var labels = registry.ActiveGrants(Now.AddSeconds(12)).Select(grant => grant.Label).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(new HashSet<string>(new[] { "tab-9", "tab-10", "tab-11" }, StringComparer.Ordinal), labels);
    }

    [Fact]
    public void TheLedgerHasATotalCap_AndRefusesWithAReasonRatherThanGrowing()
    {
        var registry = Registry(options =>
        {
            options.MaxAgentConsentGrants = 2;
            options.MaxAgentConsentGrantsPerUser = 1;
        });

        Assert.True(registry.TryGrant(TenantA, UserA1, "omni-room", null, Now, out _, out _));
        Assert.True(registry.TryGrant(TenantA, UserA2, "omni-room", null, Now, out _, out _));

        var thirdUser = Guid.Parse("11111111-0000-0000-0000-000000000003");
        Assert.False(registry.TryGrant(TenantB, thirdUser, "omni-room", null, Now, out var refused, out var error));
        Assert.Null(refused);
        Assert.False(string.IsNullOrWhiteSpace(error));
        Assert.Equal(2, registry.Count);

        // A caller already holding a slot can still RENEW at the cap — otherwise a full ledger
        // would silently drop live sessions.
        Assert.True(registry.TryGrant(TenantA, UserA1, "omni-room", null, Now.AddMinutes(1), out _, out _));
    }

    [Fact]
    public void ActiveGrants_AreOrderedOldestFirst_SoDispatchIsDeterministic()
    {
        var registry = Registry();
        Assert.True(registry.TryGrant(TenantB, UserA2, "b", null, Now.AddSeconds(2), out _, out _));
        Assert.True(registry.TryGrant(TenantA, UserA1, "a", null, Now, out _, out _));

        var order = registry.ActiveGrants(Now.AddSeconds(3)).Select(grant => grant.Label).ToList();
        Assert.Equal(new[] { "a", "b" }, order);
    }

    [Fact]
    public void UnusableIdentityOrLabel_IsRefusedWithAReason_NeverRecorded()
    {
        var registry = Registry();

        Assert.False(registry.TryGrant(Guid.Empty, UserA1, "omni-room", null, Now, out _, out var tenantError));
        Assert.False(string.IsNullOrWhiteSpace(tenantError));

        Assert.False(registry.TryGrant(TenantA, Guid.Empty, "omni-room", null, Now, out _, out var userError));
        Assert.False(string.IsNullOrWhiteSpace(userError));

        Assert.False(registry.TryGrant(TenantA, UserA1, "///", null, Now, out _, out var labelError));
        Assert.False(string.IsNullOrWhiteSpace(labelError));

        Assert.Equal(0, registry.Count);
    }

    [Fact]
    public void ConcurrentGrants_StayWithinTheCap()
    {
        var registry = Registry(options =>
        {
            options.MaxAgentConsentGrants = 8;
            options.MaxAgentConsentGrantsPerUser = 1;
        });

        Parallel.For(0, 64, i =>
        {
            var user = Guid.Parse($"11111111-0000-0000-0000-{i:D12}");
            registry.TryGrant(TenantA, user, "omni-room", null, Now, out _, out _);
        });

        // Exactly the cap: 64 racing callers, 8 slots, no torn state and no overshoot.
        Assert.Equal(8, registry.Count);
        Assert.All(registry.ActiveGrants(Now), grant => Assert.True(grant.IsSelfConsistent()));
    }
}

/// <summary>
/// A grant carries the room the dispatcher will join, so "does this room really belong to this
/// identity" is the last line of defence against putting an agent into another tenant's call.
/// </summary>
public sealed class VoiceAgentGrantConsistencyTests
{
    private static readonly Guid TenantA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid TenantB = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");
    private static readonly Guid UserA = Guid.Parse("11111111-0000-0000-0000-000000000001");

    [Fact]
    public void AWellFormedGrant_IsSelfConsistent()
    {
        Assert.True(VoiceRoomNaming.TryScopedRoom(TenantA, UserA, "omni-room", out var room, out _));
        var grant = new VoiceAgentGrant
        {
            Room = room,
            TenantId = TenantA,
            UserId = UserA,
            Label = "omni-room"
        };
        Assert.True(grant.IsSelfConsistent());
    }

    [Fact]
    public void AGrantNamingAnotherTenantsRoom_IsRejected()
    {
        Assert.True(VoiceRoomNaming.TryScopedRoom(TenantB, UserA, "omni-room", out var otherTenantsRoom, out _));
        var forged = new VoiceAgentGrant
        {
            Room = otherTenantsRoom,      // room belongs to tenant B …
            TenantId = TenantA,           // … while the grant claims tenant A
            UserId = UserA,
            Label = "omni-room"
        };
        Assert.False(forged.IsSelfConsistent());
    }

    [Fact]
    public void AGrantWhoseLabelDoesNotMatchItsRoom_IsRejected()
    {
        Assert.True(VoiceRoomNaming.TryScopedRoom(TenantA, UserA, "omni-room", out var room, out _));
        var mismatched = new VoiceAgentGrant
        {
            Room = room,
            TenantId = TenantA,
            UserId = UserA,
            Label = "some-other-label"
        };
        Assert.False(mismatched.IsSelfConsistent());
    }
}

/// <summary>
/// <c>TryParseScopedRoom</c> is the inverse of the naming function; the dispatcher uses it to
/// prove a room belongs to the identity that asked for it.
/// </summary>
public sealed class VoiceRoomNamingParseTests
{
    private static readonly Guid Tenant = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid User = Guid.Parse("11111111-0000-0000-0000-000000000001");

    [Fact]
    public void ParseRoundTripsTheNamingFunction()
    {
        Assert.True(VoiceRoomNaming.TryScopedRoom(Tenant, User, "my room/../etc", out var room, out _));
        Assert.True(VoiceRoomNaming.TryParseScopedRoom(room, out var tenantId, out var userId, out var label));

        Assert.Equal(Tenant, tenantId);
        Assert.Equal(User, userId);
        Assert.Equal("my-room-etc", label);
        Assert.Equal(room, VoiceRoomNaming.ScopedRoom(tenantId, userId, label));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("omni-room")]                                   // bare label — the old, wrong name
    [InlineData("aaaaaaaa000000000000000000000001-omni-room")]  // tenant only — the old, wrong scope
    [InlineData("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz-11111111000000000000000000000001-r")]
    [InlineData("00000000000000000000000000000000-11111111000000000000000000000001-r")]
    public void NonScopedNames_AreRefused(string? room)
    {
        Assert.False(VoiceRoomNaming.TryParseScopedRoom(room, out var tenantId, out var userId, out var label));
        Assert.Equal(Guid.Empty, tenantId);
        Assert.Equal(Guid.Empty, userId);
        Assert.Equal(string.Empty, label);
    }

    [Fact]
    public void ARoomWithNoLabel_IsRefused()
    {
        Assert.False(VoiceRoomNaming.TryParseScopedRoom($"{Tenant:N}-{User:N}-", out _, out _, out _));
    }
}
