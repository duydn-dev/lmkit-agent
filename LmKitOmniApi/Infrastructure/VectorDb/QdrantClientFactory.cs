using System.Collections.Concurrent;
using Qdrant.Client;

namespace LmKitOmniApi.Infrastructure.VectorDb;

/// <summary>
/// Builds <see cref="QdrantClient"/> instances from configuration.
/// <para>
/// Honors the URI SCHEME (<c>https://…</c> enables TLS) and an optional API key
/// (<c>VectorStore:ApiKey</c>, i.e. <c>VectorStore__ApiKey</c> in the
/// environment). Both were previously ignored: the client was always built as
/// <c>new QdrantClient(host, port)</c>, which is plaintext gRPC with no
/// credentials — silently downgrading any TLS/authenticated deployment.
/// </para>
/// </summary>
public static class QdrantClientFactory
{
    /// <summary>Default endpoint when <c>VectorStore:BaseUrl</c> is absent.</summary>
    public const string DefaultBaseUrl = "http://localhost:6334";

    private static readonly ConcurrentDictionary<string, QdrantClient> SharedClients = new(StringComparer.Ordinal);

    /// <summary>The configured Qdrant endpoint and API key (key is null when unset).</summary>
    public static (Uri Endpoint, string? ApiKey) ReadEndpoint(IConfiguration configuration)
    {
        var baseUrl = configuration["VectorStore:BaseUrl"];
        if (string.IsNullOrWhiteSpace(baseUrl)) baseUrl = DefaultBaseUrl;
        var apiKey = configuration["VectorStore:ApiKey"];
        return (new Uri(baseUrl), string.IsNullOrWhiteSpace(apiKey) ? null : apiKey);
    }

    /// <summary>
    /// A NEW client the caller owns and MUST dispose (it holds a gRPC channel).
    /// Use from long-lived singletons only.
    /// </summary>
    public static QdrantClient Create(IConfiguration configuration)
    {
        var (endpoint, apiKey) = ReadEndpoint(configuration);
        // The Uri overload derives https/plaintext from the scheme and attaches
        // the api-key header when one is supplied.
        return new QdrantClient(endpoint, apiKey);
    }

    /// <summary>
    /// A process-lifetime client SHARED by every caller using the same endpoint.
    /// For consumers that are re-created frequently and would otherwise leak one
    /// gRPC channel per instance — notably health checks, which
    /// <c>ActivatorUtilities</c> re-creates on every poll. Callers must NOT
    /// dispose it; it lives until process exit, like any other pooled channel.
    /// </summary>
    public static QdrantClient Shared(IConfiguration configuration)
    {
        var (endpoint, apiKey) = ReadEndpoint(configuration);
        var key = $"{endpoint.AbsoluteUri}|{apiKey?.Length ?? 0}";
        return SharedClients.GetOrAdd(key, _ => new QdrantClient(endpoint, apiKey));
    }
}
