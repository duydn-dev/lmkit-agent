using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace LmKitOmniApi.Tests;

/// <summary>
/// Injects a fake TCP peer address so the rate-limit and forwarded-header tests can
/// pretend several different clients (and a reverse proxy) are calling the SAME host.
///
/// <para><c>TestServer</c> never populates <c>Connection.RemoteIpAddress</c> — it is
/// null for every request — so without this every per-IP partition in the test host
/// collapses into the shared anonymous bucket and NOTHING about IP handling can be
/// observed. Worse for the forwarded-header tests specifically: with a null peer,
/// <c>ForwardedHeadersMiddleware</c> deliberately accepts the first forwarded entry
/// ("allow remoteIp to be null for servers that don't support it natively"), so the
/// known-proxy gate would appear to work while never actually being exercised.</para>
///
/// <para>Registered as an <see cref="IStartupFilter"/> rather than as ordinary
/// middleware because startup filters wrap the <c>WebApplication</c> pipeline: this
/// runs BEFORE Program.cs's first <c>app.Use…</c>, which is the only position from
/// which it can stand in for the transport and therefore be seen by
/// <c>UseForwardedHeaders</c>.</para>
/// </summary>
internal sealed class TestPeerIpStartupFilter : IStartupFilter
{
    /// <summary>Request header carrying the address to impersonate as the TCP peer.</summary>
    public const string HeaderName = "X-Test-Peer-Ip";

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) =>
        app =>
        {
            app.Use(async (context, nextMiddleware) =>
            {
                if (context.Request.Headers.TryGetValue(HeaderName, out var raw)
                    && IPAddress.TryParse(raw.ToString(), out var peer))
                {
                    context.Connection.RemoteIpAddress = peer;
                }

                await nextMiddleware(context);
            });
            next(app);
        };
}

/// <summary>
/// Builds a REAL application host (the production Program.cs pipeline, unmodified)
/// on top of the shared <see cref="LmKitApiFactory"/> seed data, with per-test
/// configuration and the peer-IP shim above. Each host owns its own rate-limiter
/// state, which is what keeps the tests independent of one another.
/// </summary>
internal static class RateLimitTestHost
{
    /// <summary>An unknown share token: always 404, never touches the seeded data.</summary>
    private const string UnknownShareToken = "not-a-real-share-token";

    public static WebApplicationFactory<Program> Create(
        LmKitApiFactory parent,
        IDictionary<string, string?>? configuration = null)
        => parent.WithWebHostBuilder(builder =>
        {
            foreach (var (key, value) in configuration ?? new Dictionary<string, string?>())
            {
                // UseSetting, NOT ConfigureAppConfiguration. WebApplicationFactory relays
                // HOST configuration to the entry point as command-line arguments, so
                // UseSetting values are present in builder.Configuration while Program.cs's
                // top-level statements run. ConfigureAppConfiguration is only merged during
                // builder.Build(), i.e. AFTER the rate-limit policies and the forwarded-header
                // validation have already read their settings — an override made that way is
                // silently ignored (verified: it left the AI budget at its default of 10).
                builder.UseSetting(key, value);
            }

            builder.ConfigureServices(services =>
                services.AddSingleton<IStartupFilter, TestPeerIpStartupFilter>());
        });

    /// <summary>
    /// One call against an <c>ai-agent</c>-limited endpoint. The body is deliberately
    /// invalid so a permitted request stops at model-free validation (400) — the only
    /// distinction that matters here is 400 (permitted) vs 429 (throttled).
    /// </summary>
    public static async Task<HttpStatusCode> AiRequestAsync(HttpClient client)
    {
        using var response = await client.PostAsJsonAsync(
            "/api/chat/stream",
            new { sessionId = Guid.Empty, message = "" });
        return response.StatusCode;
    }

    /// <summary>
    /// One anonymous read of a non-existent share link (<c>SharePolicy</c>: 30 permits
    /// per 60s per IP). Permitted → 404, throttled → 429. Chosen over the login
    /// endpoint because its 60s window leaves no room for timing flakiness.
    /// </summary>
    public static async Task<HttpStatusCode> AnonymousShareReadAsync(
        HttpClient client,
        string? peerIp = null,
        string? forwardedFor = null)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/api/share/chat/{UnknownShareToken}");
        if (peerIp is not null) request.Headers.Add(TestPeerIpStartupFilter.HeaderName, peerIp);
        if (forwardedFor is not null) request.Headers.Add("X-Forwarded-For", forwardedFor);

        using var response = await client.SendAsync(request);
        return response.StatusCode;
    }

    /// <summary>
    /// Drains <c>SharePolicy</c>'s 30 permits and returns the status of the 31st call.
    /// <paramref name="forwardedForFactory"/> varies the spoofed header per call so a
    /// test can prove the header did (or did not) split the caller into buckets.
    /// </summary>
    public static async Task<HttpStatusCode> DrainSharePolicyAsync(
        HttpClient client,
        string peerIp,
        Func<int, string?>? forwardedForFactory = null)
    {
        const int permitLimit = 30;
        for (var attempt = 0; attempt < permitLimit; attempt++)
        {
            var status = await AnonymousShareReadAsync(
                client,
                peerIp,
                forwardedForFactory?.Invoke(attempt));
            Assert.Equal(HttpStatusCode.NotFound, status);
        }

        return await AnonymousShareReadAsync(
            client,
            peerIp,
            forwardedForFactory?.Invoke(permitLimit));
    }

    /// <summary>Flattens an exception chain so a startup failure can be asserted on.</summary>
    public static string Describe(Exception error)
    {
        var text = new System.Text.StringBuilder();
        for (Exception? current = error; current is not null; current = current.InnerException)
            text.Append(current.Message).Append(" | ");
        return text.ToString();
    }
}
