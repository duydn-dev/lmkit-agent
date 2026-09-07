using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace LmKitOmniApi.Tests;

/// <summary>
/// Pins the isolation a host built with <c>WithWebHostBuilder</c> must have from the
/// factory it was derived from.
///
/// <para><b>The defect.</b> Giving every <c>WebApplicationFactory</c> its own
/// data-protection key ring closed a real, reproduced race: <c>Program.cs</c> reads
/// <c>DataProtection:KeyPath</c> in a top-level statement and defaults it to ONE directory
/// shared by every host in the process, and two hosts creating that ring at the same time
/// leave a half-written key XML behind — the next protected operation throws, login answers
/// 500, and the whole fixture dies ("Fixture login failed: InternalServerError"). The fix
/// was per-factory: <c>ConfigureWebHost</c> writes a unique path. But
/// <c>WithWebHostBuilder</c> REPLAYS the parent's <c>ConfigureWebHost</c> and then layers the
/// derived overrides on top, so a derived host that did not name its own path silently ran
/// on the parent's ring — the same defect, one call away, and live rather than latent as
/// soon as any derived host outlives a single request.</para>
///
/// <para>These tests are what stops the next <c>WithWebHostBuilder</c> call from
/// reintroducing it: the isolation is asserted through the RUNNING host's
/// <c>IConfiguration</c> and by two live hosts performing real protected operations at the
/// same time, not by inspecting the helper that is supposed to arrange it.</para>
/// </summary>
public sealed class DerivedTestHostIsolationTests
{
    [Fact]
    public void DerivedHost_DoesNotRunOnTheParentsKeyRing()
    {
        using var parent = new LmKitApiFactory();
        using var derived = RateLimitTestHost.Create(parent);

        var parentPath = parent.Services.GetRequiredService<IConfiguration>()
            [TestHostConfiguration.DataProtectionKeyPathSetting];
        var derivedPath = derived.Services.GetRequiredService<IConfiguration>()
            [TestHostConfiguration.DataProtectionKeyPathSetting];

        Assert.False(string.IsNullOrWhiteSpace(parentPath));
        Assert.False(string.IsNullOrWhiteSpace(derivedPath));
        Assert.NotEqual(parentPath, derivedPath);
    }

    /// <summary>
    /// Two derived hosts of the same parent must not share a ring either — the settings
    /// that make a derived host worth building (a tighter budget, a trust list) say nothing
    /// about key storage, so nothing else would keep them apart.
    /// </summary>
    [Fact]
    public void TwoDerivedHostsOfOneParent_GetDifferentKeyRings()
    {
        using var parent = new LmKitApiFactory();
        using var first = RateLimitTestHost.Create(parent, new Dictionary<string, string?>
        {
            ["RateLimiting:AiRequestsPerWindow"] = "2"
        });
        using var second = RateLimitTestHost.Create(parent, new Dictionary<string, string?>
        {
            ["RateLimiting:AiRequestsPerWindow"] = "3"
        });

        var firstPath = first.Services.GetRequiredService<IConfiguration>()
            [TestHostConfiguration.DataProtectionKeyPathSetting];
        var secondPath = second.Services.GetRequiredService<IConfiguration>()
            [TestHostConfiguration.DataProtectionKeyPathSetting];

        Assert.NotEqual(firstPath, secondPath);
        // ...and the derived overrides still land, so isolating the ring did not cost the
        // one thing the derived host exists for.
        Assert.Equal("2", first.Services.GetRequiredService<IConfiguration>()["RateLimiting:AiRequestsPerWindow"]);
        Assert.Equal("3", second.Services.GetRequiredService<IConfiguration>()["RateLimiting:AiRequestsPerWindow"]);
    }

    /// <summary>
    /// A derived ring lives INSIDE the parent's directory, which is what makes the parent's
    /// recursive delete on <c>Dispose</c> the cleanup for both.
    /// <c>FileSystemXmlRepository</c> enumerates <c>*.xml</c> in its own directory only, so
    /// nesting isolates the keys exactly as a sibling directory would.
    /// </summary>
    [Fact]
    public void DerivedKeyRing_IsNestedInTheParentsSoItIsCleanedUpWithIt()
    {
        using var parent = new LmKitApiFactory();
        using var derived = RateLimitTestHost.Create(parent);

        var derivedPath = derived.Services.GetRequiredService<IConfiguration>()
            [TestHostConfiguration.DataProtectionKeyPathSetting]!;

        Assert.StartsWith(
            parent.DataProtectionKeyPath + Path.DirectorySeparatorChar,
            derivedPath,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The behavioural half, and the only formulation the plumbing cannot fool: the parent
    /// and two derived hosts all perform a REAL protected operation — a login, whose auth
    /// cookie is produced by the data-protection stack — while all three are live. On one
    /// shared ring this is the shape that produced the 500.
    /// </summary>
    [Fact]
    public async Task ParentAndDerivedHosts_AllProtectAndUnprotectWhileTheOthersAreLive()
    {
        using var parent = new LmKitApiFactory();
        parent.EnsureSeeded();
        using var first = RateLimitTestHost.Create(parent);
        using var second = RateLimitTestHost.Create(parent);

        var logins = await Task.WhenAll(
            LoginAsync(parent),
            LoginAsync(first),
            LoginAsync(second));

        Assert.All(logins, status => Assert.Equal(HttpStatusCode.OK, status));
    }

    private static async Task<HttpStatusCode> LoginAsync(WebApplicationFactory<Program> factory)
    {
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true
        });
        using var login = await client.PostAsJsonAsync("/api/auth/login", new
        {
            email = LmKitApiFactory.Email,
            password = LmKitApiFactory.Password
        });
        return login.StatusCode;
    }
}
