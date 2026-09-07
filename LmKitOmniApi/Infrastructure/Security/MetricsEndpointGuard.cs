namespace LmKitOmniApi.Infrastructure.Security;

/// <summary>
/// Admin-only gate in front of the OpenTelemetry Prometheus scraping endpoint.
///
/// WHY THIS EXISTS AS A NAMED HELPER: the guard MUST cover the same path space as the
/// exporter it protects. <c>UseOpenTelemetryPrometheusScrapingEndpoint()</c> (the no-arg
/// overload, OpenTelemetry.Exporter.Prometheus.AspNetCore 1.16.0-beta.1) registers its
/// branch with <c>IApplicationBuilder.Map("/metrics", ...)</c>, and <c>Map</c> matches on
/// <see cref="PathString.StartsWithSegments(PathString)"/> — the whole <c>/metrics</c>
/// SUBTREE, not one exact path.
///
/// The original guard matched with <c>Path.Equals("/metrics")</c>. That left
/// <c>GET /metrics/</c> and <c>GET /metrics/anything</c> outside the guard branch but still
/// inside the exporter's branch: a full anonymous Prometheus scrape. Matching with
/// <see cref="PathString.StartsWithSegments(PathString, StringComparison)"/> makes the two
/// path spaces identical, and segment matching still refuses to over-block sibling routes
/// such as <c>/metricsdashboard</c>.
///
/// Placement: call this AFTER <c>UseAuthentication()</c>/<c>UseAuthorization()</c> and
/// immediately BEFORE <c>UseOpenTelemetryPrometheusScrapingEndpoint()</c>, so
/// <see cref="HttpContext.User"/> is populated by the time the role check runs.
/// </summary>
public static class MetricsEndpointGuard
{
    /// <summary>Root of the path subtree the Prometheus exporter serves.</summary>
    public const string MetricsPathPrefix = "/metrics";

    /// <summary>Role required to scrape metrics.</summary>
    public const string RequiredRole = "Admin";

    /// <summary>
    /// True for every request the Prometheus exporter would answer: <c>/metrics</c>,
    /// <c>/metrics/</c> and <c>/metrics/&lt;anything&gt;</c> — but NOT <c>/metricsfoo</c>.
    /// </summary>
    public static bool IsMetricsRequest(HttpContext context) =>
        context.Request.Path.StartsWithSegments(MetricsPathPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Short-circuits every non-Admin request to the <c>/metrics</c> subtree with
    /// 403 Forbidden before it can reach the exporter.
    /// </summary>
    public static IApplicationBuilder UseMetricsEndpointGuard(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        return app.UseWhen(IsMetricsRequest, metricsApp => metricsApp.Use(async (context, next) =>
        {
            if (context.User.Identity?.IsAuthenticated != true || !context.User.IsInRole(RequiredRole))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            await next(context);
        }));
    }
}
