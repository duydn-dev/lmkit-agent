using LmKitOmniApi.Infrastructure.AI.Security;
using Microsoft.Extensions.Options;

namespace LmKitOmniApi.Tests;

/// <summary>
/// The DB egress guard is the SSRF backstop for raw database TCP (the HTTP SSRF
/// guard doesn't cover it). Internal/loopback/link-local targets must always be
/// refused; an optional allowlist further narrows permitted hosts. Literal IPs are
/// used throughout so no DNS lookup runs — the tests are deterministic + offline.
/// </summary>
public class DbEgressValidatorTests
{
    private static DbEgressValidator Create(params string[] allowedHosts) =>
        new(Options.Create(new DatabaseAgentOptions { AllowedHosts = allowedHosts.ToList() }));

    [Theory]
    [InlineData("127.0.0.1")]          // loopback
    [InlineData("10.0.0.5")]           // private class A
    [InlineData("192.168.1.10")]       // private class C
    [InlineData("172.16.5.5")]         // private class B
    [InlineData("169.254.169.254")]    // link-local metadata endpoint
    [InlineData("::1")]                // IPv6 loopback
    public async Task Blocks_InternalAddresses(string host)
    {
        var result = await Create().ValidateHostAsync(host, CancellationToken.None);
        Assert.False(result.IsAllowed);
    }

    [Fact]
    public async Task Allows_PublicAddress_WhenNoAllowlist()
    {
        var result = await Create().ValidateHostAsync("8.8.8.8", CancellationToken.None);
        Assert.True(result.IsAllowed);
    }

    [Fact]
    public async Task Allowlist_PermitsListedHost_AndRefusesOthers()
    {
        var validator = Create("8.8.8.8");
        Assert.True((await validator.ValidateHostAsync("8.8.8.8", CancellationToken.None)).IsAllowed);

        var denied = await validator.ValidateHostAsync("1.1.1.1", CancellationToken.None);
        Assert.False(denied.IsAllowed);
    }

    [Fact]
    public async Task Allowlist_StillBlocksInternal_EvenIfListed()
    {
        // An internal address is refused regardless of the allowlist (guard wins).
        var result = await Create("127.0.0.1").ValidateHostAsync("127.0.0.1", CancellationToken.None);
        Assert.False(result.IsAllowed);
    }

    [Fact]
    public async Task Blocks_EmptyHost()
    {
        var result = await Create().ValidateHostAsync("   ", CancellationToken.None);
        Assert.False(result.IsAllowed);
    }
}
