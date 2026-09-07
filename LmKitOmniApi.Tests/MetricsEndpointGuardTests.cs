using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using LmKitOmniApi.Infrastructure.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LmKitOmniApi.Tests;

/// <summary>
/// The Admin gate on the Prometheus scraping endpoint.
///
/// THE BUG: the guard matched with <c>Path.Equals("/metrics")</c>, but the exporter's no-arg
/// <c>UseOpenTelemetryPrometheusScrapingEndpoint()</c> registers with
/// <c>IApplicationBuilder.Map("/metrics", ...)</c>, which matches by PREFIX
/// (<c>StartsWithSegments</c>). <c>GET /metrics/</c> and <c>GET /metrics/anything</c>
/// therefore skipped the guard branch while still landing in the exporter branch — a full
/// anonymous Prometheus scrape from any unauthenticated caller.
///
/// The pipeline below reproduces production ordering exactly, including the prefix-mapped
/// exporter stand-in, so the guard is tested against the same path space it must cover.
/// </summary>
public sealed class MetricsEndpointGuardTests : IAsyncLifetime
{
    private const string ExporterBody = "# HELP lmkit_secret_metric\nlmkit_secret_metric 42\n";
    private const string MainPipelineBody = "main-pipeline";

    /// <summary>Every path the exporter's prefix Map would answer.</summary>
    public static TheoryData<string> ExporterPaths() => new() { "/metrics", "/metrics/", "/metrics/anything" };

    private IHost _host = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _host = await new HostBuilder()
            .ConfigureWebHost(webBuilder => webBuilder
                .UseTestServer()
                .ConfigureServices(services => services
                    .AddAuthentication(StubAuthenticationHandler.SchemeName)
                    .AddScheme<AuthenticationSchemeOptions, StubAuthenticationHandler>(
                        StubAuthenticationHandler.SchemeName, _ => { }))
                .Configure(app =>
                {
                    app.UseAuthentication();
                    app.UseMetricsEndpointGuard();

                    // Stand-in for UseOpenTelemetryPrometheusScrapingEndpoint(): the real
                    // exporter registers its branch the same way, with a PREFIX Map.
                    app.Map("/metrics", exporter => exporter.Run(
                        context => context.Response.WriteAsync(ExporterBody)));

                    app.Run(context => context.Response.WriteAsync(MainPipelineBody));
                }))
            .StartAsync();

        _client = _host.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _host.StopAsync();
        _host.Dispose();
    }

    [Theory]
    [MemberData(nameof(ExporterPaths))]
    public async Task AnonymousCaller_IsRefusedAcrossTheWholeMetricsSubtree(string path)
    {
        var response = await _client.GetAsync(path);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.DoesNotContain("lmkit_secret_metric", body, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(ExporterPaths))]
    public async Task AuthenticatedNonAdmin_IsRefusedAcrossTheWholeMetricsSubtree(string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add(StubAuthenticationHandler.RoleHeader, "User");

        var response = await _client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.DoesNotContain("lmkit_secret_metric", body, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(ExporterPaths))]
    public async Task AdminCaller_StillReachesTheExporter(string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add(StubAuthenticationHandler.RoleHeader, "Admin");

        var response = await _client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("lmkit_secret_metric", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// Segment matching, not string prefixing: the guard must not swallow sibling routes that
    /// merely start with the same characters, and must leave the rest of the app alone.
    /// </summary>
    [Theory]
    [InlineData("/metricsdashboard")]
    [InlineData("/api/chat/sessions")]
    [InlineData("/")]
    public async Task UnrelatedPaths_AreNotGuarded(string path)
    {
        var response = await _client.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(MainPipelineBody, await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("/metrics", true)]
    [InlineData("/metrics/", true)]
    [InlineData("/metrics/anything", true)]
    [InlineData("/METRICS/x", true)]
    [InlineData("/metricsdashboard", false)]
    [InlineData("/api/metrics", false)]
    [InlineData("/", false)]
    public void IsMetricsRequest_MatchesTheExporterPathSpace(string path, bool expected)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;

        Assert.Equal(expected, MetricsEndpointGuard.IsMetricsRequest(context));
    }

    /// <summary>Authenticates from a test header so anonymous / non-Admin / Admin are all reachable.</summary>
    private sealed class StubAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public const string SchemeName = "StubAuth";
        public const string RoleHeader = "X-Test-Role";

        public StubAuthenticationHandler(
            IOptionsMonitor<AuthenticationSchemeOptions> options,
            ILoggerFactory logger,
            UrlEncoder encoder)
            : base(options, logger, encoder)
        {
        }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue(RoleHeader, out var role) || string.IsNullOrWhiteSpace(role))
                return Task.FromResult(AuthenticateResult.NoResult());

            var identity = new ClaimsIdentity(
                [new Claim(ClaimTypes.Name, "test-user"), new Claim(ClaimTypes.Role, role.ToString())],
                SchemeName);

            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
        }
    }
}
