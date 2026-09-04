namespace LmKitOmniApi.Infrastructure.AI.Web;

/// <summary>
/// Narrow seam over the actual LM-Kit.NET fetch-and-read (the real network call),
/// mirroring the <see cref="Security.IProcessRunner"/> seam used by the Python and
/// browser sandboxes. Isolating the LM-Kit dependency here keeps
/// <see cref="LmKitWebReadService"/> — with its SSRF pre-flight, length cap, enable
/// gating and citation formatting — a pure, hermetically testable policy layer: a
/// fake reader is injected in tests so no real network (and no LM-Kit model/native
/// initialization) is ever touched, while the single real implementation
/// (<see cref="LmKitWebPageReader"/>) is exercised live only.
/// </summary>
public interface IWebPageReader
{
    /// <summary>
    /// Fetches <paramref name="url"/> under a restrictive egress policy and returns
    /// its main content as clean Markdown (title, canonical URL, source domain,
    /// publication date when stated). The URL is assumed already SSRF-validated by the
    /// caller; the underlying egress policy re-validates every hop regardless.
    /// </summary>
    Task<string> ReadAsync(string url, CancellationToken ct = default);
}
