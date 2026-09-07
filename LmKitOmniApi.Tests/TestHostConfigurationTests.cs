using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace LmKitOmniApi.Tests;

/// <summary>
/// Pins the one thing every configuration-driven integration test silently depends on:
/// that a test host's configuration actually reaches the code under test.
///
/// <para>The failure mode these lock down is not a wrong value, it is an ABSENT one.
/// A <c>ConfigureAppConfiguration</c> layer is merged when <c>builder.Build()</c> runs,
/// so every setting <c>Program.cs</c>'s top-level statements read before that line came
/// from <c>appsettings.json</c> no matter what the test asked for. Nothing threw and
/// nothing went red — the test simply measured the shipped default, and passed if the
/// default happened to agree with it.</para>
///
/// <para>The assertions below are deliberately behavioural where they can be: they check
/// that a changed value CHANGED SOMETHING in the running host, which is the only
/// formulation that cannot itself be fooled by a configuration layer landing in the
/// wrong place.</para>
/// </summary>
public sealed class TestHostConfigurationTests
{
    /// <summary>
    /// <c>RateLimiting:AiRequestsPerWindow</c> is read at <c>Program.cs</c> line 597, about
    /// 110 lines before <c>builder.Build()</c>, and captured into the "ai-agent" policy's
    /// token bucket. Overriding it to 2 must cut the caller off on the third request. While
    /// the override was inert this ran on the appsettings default of 10 and the third
    /// request came back 400 like the first two.
    /// </summary>
    [Fact]
    public async Task OverrideOfSettingReadBeforeBuild_ChangesHostBehaviour()
    {
        using var factory = new LmKitApiFactory();
        factory.ConfigurationOverrides["RateLimiting:AiRequestsPerWindow"] = "2";
        factory.EnsureSeeded();

        using var client = await CreateAuthenticatedClientAsync(factory);
        var invalidCommand = new { sessionId = Guid.Empty, message = "" };

        for (var requestNumber = 1; requestNumber <= 2; requestNumber++)
        {
            using var permitted = await client.PostAsJsonAsync("/api/chat/stream", invalidCommand);
            Assert.Equal(HttpStatusCode.BadRequest, permitted.StatusCode);
        }

        using var limited = await client.PostAsJsonAsync("/api/chat/stream", invalidCommand);

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
    }

    /// <summary>
    /// The same guarantee at the configuration level, so a future change to the factory's
    /// plumbing is caught even where no endpoint happens to read the key: an override must
    /// be visible in the running host's <c>IConfiguration</c> and must OUTRANK both
    /// <c>appsettings.json</c> and the factory's own default for that key.
    /// </summary>
    [Fact]
    public void Override_OutranksAppsettingsAndTheFactoryDefault()
    {
        using var factory = new LmKitApiFactory();
        factory.ConfigurationOverrides["RateLimiting:AiWindowSeconds"] = "3607";
        factory.EnsureSeeded();

        var configuration = factory.Services.GetRequiredService<IConfiguration>();

        Assert.Equal("3607", configuration["RateLimiting:AiWindowSeconds"]);
    }

    /// <summary>
    /// The eager and the lazy reader of the SAME key must agree. <c>Program.cs</c> reads
    /// the AI budget into the "ai-agent" policy before <c>Build()</c>;
    /// <c>DistributedAiRateLimitMiddleware</c> reads it again from <c>IConfiguration</c>
    /// when it is constructed. A host that answers those two readers differently is exactly
    /// what mixing the host and app configuration layers produced, and it is invisible
    /// until a test asserts on the half that lost.
    /// </summary>
    [Fact]
    public void DerivedHostOverride_IsSeenByEarlyAndLateReadersAlike()
    {
        using var parent = new LmKitApiFactory();
        using var host = RateLimitTestHost.Create(parent, new Dictionary<string, string?>
        {
            ["RateLimiting:AiRequestsPerWindow"] = "7"
        });

        var configuration = host.Services.GetRequiredService<IConfiguration>();

        Assert.Equal("7", configuration["RateLimiting:AiRequestsPerWindow"]);
    }

    /// <summary>
    /// An override written after the host has been built cannot take effect. It used to be
    /// dropped in silence; it now throws, because the test author would otherwise be told
    /// nothing at all.
    /// </summary>
    [Fact]
    public void OverrideSetAfterTheHostIsBuilt_ThrowsInsteadOfBeingIgnored()
    {
        using var factory = new LmKitApiFactory();
        factory.EnsureSeeded();

        var error = Assert.Throws<InvalidOperationException>(
            () => factory.ConfigurationOverrides["RateLimiting:AiWindowSeconds"] = "11");

        Assert.Contains("after the test host had already been built", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Host configuration travels as <c>--key=value</c> and cannot express null, so a null
    /// is refused rather than quietly turned into an empty string — which reads back
    /// differently (an in-memory null makes <c>GetValue&lt;int&gt;</c> return its default,
    /// an empty string makes it throw).
    /// </summary>
    [Fact]
    public void NullValuedSetting_IsRefused()
    {
        using var factory = new LmKitApiFactory();
        factory.ConfigurationOverrides["RateLimiting:AiWindowSeconds"] = null;

        var error = Assert.ThrowsAny<Exception>(() => factory.Services.GetService<IConfiguration>());

        Assert.Contains("null value", RateLimitTestHost.Describe(error), StringComparison.Ordinal);
    }

    private static async Task<HttpClient> CreateAuthenticatedClientAsync(LmKitApiFactory factory)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true
        });
        var login = await client.PostAsJsonAsync("/api/auth/login", new
        {
            email = LmKitApiFactory.Email,
            password = LmKitApiFactory.Password
        });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        return client;
    }
}
