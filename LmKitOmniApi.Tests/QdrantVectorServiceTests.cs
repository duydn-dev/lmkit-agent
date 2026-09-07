using System.Net.Sockets;
using LmKitOmniApi.Infrastructure.VectorDb;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Qdrant.Client;

namespace LmKitOmniApi.Tests;

/// <summary>
/// Coverage for the Qdrant adapter's contract fixes:
/// <list type="bullet">
///   <item>the endpoint honors <c>https</c> and an optional API key (both were ignored),</item>
///   <item>sparse-search failures PROPAGATE instead of being swallowed by a bare
///         <c>catch (Exception) { }</c> — which is what made
///         <c>RagPipelineService.PerformKeywordSearchFallbackAsync</c> dead code,</item>
///   <item>and <c>EnsureCollectionExistsAsync</c> creates the full-text payload
///         index that <c>Match.Text</c> requires (live, skipped without Qdrant).</item>
/// </list>
/// </summary>
public sealed class QdrantVectorServiceTests
{
    private const string LiveHost = "localhost";
    private const int LivePort = 6334;

    private static IConfiguration ConfigWith(string? baseUrl = null, string? apiKey = null)
    {
        var values = new Dictionary<string, string?>();
        if (baseUrl is not null) values["VectorStore:BaseUrl"] = baseUrl;
        if (apiKey is not null) values["VectorStore:ApiKey"] = apiKey;
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    // --- Endpoint configuration --------------------------------------------------

    [Fact]
    public void ReadEndpoint_DefaultsToLocalPlaintextGrpc()
    {
        var (endpoint, apiKey) = QdrantClientFactory.ReadEndpoint(ConfigWith());
        Assert.Equal(new Uri(QdrantClientFactory.DefaultBaseUrl), endpoint);
        Assert.Null(apiKey);
    }

    /// <summary>
    /// The scheme used to be discarded entirely — the client was always built as
    /// <c>new QdrantClient(uri.Host, uri.Port)</c>, i.e. plaintext, so an
    /// <c>https://</c> endpoint was silently downgraded.
    /// </summary>
    [Fact]
    public void ReadEndpoint_PreservesHttpsSchemeAndApiKey()
    {
        var (endpoint, apiKey) = QdrantClientFactory.ReadEndpoint(
            ConfigWith("https://qdrant.internal:6334", "s3cr3t"));

        Assert.Equal(Uri.UriSchemeHttps, endpoint.Scheme);
        Assert.Equal("qdrant.internal", endpoint.Host);
        Assert.Equal(6334, endpoint.Port);
        Assert.Equal("s3cr3t", apiKey);
    }

    [Fact]
    public void ReadEndpoint_TreatsBlankApiKeyAsAbsent()
    {
        var (_, apiKey) = QdrantClientFactory.ReadEndpoint(ConfigWith("http://localhost:6334", "   "));
        Assert.Null(apiKey);
    }

    /// <summary>
    /// Health checks are re-created by <c>ActivatorUtilities</c> on every poll;
    /// the shared client keeps that from leaking one gRPC channel per poll.
    /// </summary>
    [Fact]
    public void SharedClient_IsReusedAcrossCallsForTheSameEndpoint()
    {
        var config = ConfigWith("http://127.0.0.1:6399");
        Assert.Same(QdrantClientFactory.Shared(config), QdrantClientFactory.Shared(ConfigWith("http://127.0.0.1:6399")));
    }

    [Fact]
    public void SharedClient_DiffersPerEndpoint()
    {
        Assert.NotSame(
            QdrantClientFactory.Shared(ConfigWith("http://127.0.0.1:6397")),
            QdrantClientFactory.Shared(ConfigWith("http://127.0.0.1:6398")));
    }

    // --- Failures are no longer swallowed ---------------------------------------

    /// <summary>
    /// Both sparse-search entry points used to end in <c>catch (Exception) { }</c>,
    /// so a missing payload index (or a dead server) silently produced "no keyword
    /// hits" forever and the caller's documented dense fallback never ran. They
    /// must throw now.
    /// </summary>
    [Fact]
    public async Task SearchByPayloadFilter_PropagatesFailure_SoTheDenseFallbackIsReachable()
    {
        using var service = DeadService();

        await Assert.ThrowsAnyAsync<Exception>(() => service.SearchByPayloadFilterAsync(
            "collection", "Keywords", ["alpha", "beta"], "AccessScope", "tenant", 20, Timeout()));
    }

    [Fact]
    public async Task SearchByPayloadWithinDocuments_PropagatesFailure()
    {
        using var service = DeadService();

        await Assert.ThrowsAnyAsync<Exception>(() => service.SearchByPayloadWithinDocumentsAsync(
            "collection", "Keywords", ["alpha"], "TenantId", "tenant", "DocumentId", ["doc-1"], 20, Timeout()));
    }

    /// <summary>Guard clauses still short-circuit before any network call.</summary>
    [Fact]
    public async Task SparseSearch_WithNothingToMatch_ReturnsEmptyWithoutTouchingQdrant()
    {
        using var service = DeadService();

        Assert.Empty(await service.SearchByPayloadFilterAsync(
            "collection", "Keywords", [], "AccessScope", "tenant", 20, Timeout()));
        Assert.Empty(await service.SearchByPayloadWithinDocumentsAsync(
            "collection", "Keywords", ["alpha"], "TenantId", "tenant", "DocumentId", [], 20, Timeout()));
        Assert.Empty(await service.SearchByPayloadWithinDocumentsAsync(
            "collection", "Keywords", ["alpha"], "TenantId", "", "DocumentId", ["doc-1"], 20, Timeout()));
    }

    /// <summary>A port nothing listens on: the gRPC call fails fast (connection refused).</summary>
    private static QdrantVectorService DeadService() =>
        new(ConfigWith("http://127.0.0.1:1"), NullLogger<QdrantVectorService>.Instance);

    private static CancellationToken Timeout() => new CancellationTokenSource(TimeSpan.FromSeconds(20)).Token;

    // --- LIVE: the payload index the hybrid path depends on ----------------------

    private static async Task<bool> QdrantIsReachableAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            using var probe = new TcpClient();
            await probe.ConnectAsync(LiveHost, LivePort, cts.Token);
            return probe.Connected;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// LIVE check (skipped without a Qdrant on localhost:6334): ensuring a
    /// collection must also create the FULL-TEXT payload index on "Keywords".
    /// Nothing in the repo ever created one, so every <c>Match.Text</c> filter
    /// failed and "hybrid" retrieval was dense-only in perpetuity.
    /// </summary>
    [SkippableFact]
    public async Task EnsureCollectionExists_CreatesTheFullTextPayloadIndex()
    {
        Skip.IfNot(await QdrantIsReachableAsync(),
            $"Qdrant is not listening on {LiveHost}:{LivePort} — skipping the live check.");

        var collection = $"lmkit-index-test-{Guid.NewGuid():N}";
        var config = ConfigWith($"http://{LiveHost}:{LivePort}");
        using var service = new QdrantVectorService(config, NullLogger<QdrantVectorService>.Instance);
        using var client = QdrantClientFactory.Create(config);

        try
        {
            await service.EnsureCollectionExistsAsync(collection, 8, Timeout());

            var info = await client.GetCollectionInfoAsync(collection, Timeout());
            Assert.True(info.PayloadSchema.ContainsKey(QdrantVectorService.FullTextIndexField),
                $"No payload index on '{QdrantVectorService.FullTextIndexField}'; Match.Text filters cannot work.");
            foreach (var field in QdrantVectorService.KeywordIndexFields)
                Assert.True(info.PayloadSchema.ContainsKey(field), $"No payload index on '{field}'.");

            // Idempotent: a FRESH service (empty per-process index cache) must be
            // able to re-ensure an existing collection without failing.
            using var second = new QdrantVectorService(config, NullLogger<QdrantVectorService>.Instance);
            await second.EnsureCollectionExistsAsync(collection, 8, Timeout());
        }
        finally
        {
            await client.DeleteCollectionAsync(collection, cancellationToken: Timeout());
        }
    }
}
