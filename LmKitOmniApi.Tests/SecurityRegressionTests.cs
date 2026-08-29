using System.Net;
using System.Text;
using LmKitOmniApi.Infrastructure.Security;
using LmKitOmniApi.Infrastructure.Health;
using LmKitOmniApi.Infrastructure.AI.Resilience;
using LmKitOmniApi.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace LmKitOmniApi.Tests;

public sealed class SecurityRegressionTests
{
    [Theory]
    [InlineData("huggingface.co", true)]
    [InlineData("cdn-lfs.huggingface.co", true)]
    [InlineData("cas-bridge.xethub.hf.co", true)]
    [InlineData("huggingface.co.attacker.example", false)]
    [InlineData("localhost", false)]
    [InlineData("127.0.0.1", false)]
    public void ModelHostAllowlist_RequiresExactTrustedSuffix(string host, bool expected) =>
        Assert.Equal(expected, LmModelManager.IsTrustedModelHost(host));

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("10.10.0.1", true)]
    [InlineData("172.16.0.1", true)]
    [InlineData("192.168.1.1", true)]
    [InlineData("169.254.1.1", true)]
    [InlineData("8.8.8.8", false)]
    public void ModelAddressGuard_BlocksPrivateNetworks(string address, bool expected) =>
        Assert.Equal(expected, LmModelManager.IsPrivateOrLocalAddress(IPAddress.Parse(address)));

    [Fact]
    public async Task UploadSignatureValidator_AcceptsRealPdfHeader()
    {
        await using var stream = new MemoryStream(Encoding.ASCII.GetBytes("%PDF-1.7\ncontent"));
        var file = new FormFile(stream, 0, stream.Length, "file", "report.pdf");
        Assert.True(await UploadFileValidator.HasExpectedSignatureAsync(file, ".pdf", CancellationToken.None));
    }

    [Fact]
    public async Task UploadSignatureValidator_RejectsRenamedExecutable()
    {
        await using var stream = new MemoryStream(new byte[] { 0x4D, 0x5A, 0x90, 0x00 });
        var file = new FormFile(stream, 0, stream.Length, "file", "malware.pdf");
        Assert.False(await UploadFileValidator.HasExpectedSignatureAsync(file, ".pdf", CancellationToken.None));
    }

    [Fact]
    public void McpHeaderProtector_EncryptsAndRoundTrips()
    {
        var provider = new EphemeralDataProtectionProvider();
        var protector = new McpHeaderProtector(provider);
        const string secret = "{\"Authorization\":\"Bearer secret-token\"}";

        var encrypted = protector.Protect(secret);

        Assert.StartsWith("dp:v1:", encrypted);
        Assert.DoesNotContain("secret-token", encrypted);
        Assert.Equal(secret, protector.Unprotect(encrypted));
    }

    [Fact]
    public async Task ResiliencePolicy_DoesNotRetryUnsafeAction()
    {
        var cache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        var policy = new AgentResiliencePolicy(NullLogger<AgentResiliencePolicy>.Instance, cache);
        var attempts = 0;

        var result = await policy.ExecuteWithResilienceAsync(
            "MCP_WRITE_TEST",
            _ =>
            {
                attempts++;
                throw new InvalidOperationException("side effect may already have happened");
            },
            "fallback",
            retrySafe: false);

        Assert.Equal("fallback", result);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task RequiredResiliencePolicy_DoesNotRetryUnsafeAction()
    {
        var cache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        var policy = new AgentResiliencePolicy(NullLogger<AgentResiliencePolicy>.Instance, cache);
        var attempts = 0;

        await Assert.ThrowsAsync<InvalidOperationException>(() => policy.ExecuteRequiredWithResilienceAsync<string>(
            "APPROVED_WRITE_TEST",
            _ =>
            {
                attempts++;
                throw new InvalidOperationException("write failed ambiguously");
            },
            retrySafe: false));

        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task CircuitBreaker_CountsConcurrentFailuresAtomicallyAndIsolatesTenants()
    {
        var cache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        var policy = new AgentResiliencePolicy(NullLogger<AgentResiliencePolicy>.Instance, cache);
        var releaseFailures = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = 0;

        var failures = Enumerable.Range(0, 5).Select(_ => policy.ExecuteWithResilienceAsync(
            "SHARED_TOOL",
            async _ =>
            {
                Interlocked.Increment(ref started);
                await releaseFailures.Task;
                throw new InvalidOperationException("simulated dependency failure");
            },
            "fallback",
            retrySafe: false,
            isolationKey: "tenant-a:shared-tool")).ToArray();

        await WaitUntilAsync(() => Volatile.Read(ref started) == 5);
        releaseFailures.SetResult();
        Assert.All(await Task.WhenAll(failures), result => Assert.Equal("fallback", result));

        var blockedWasInvoked = false;
        var blocked = await policy.ExecuteWithResilienceAsync(
            "SHARED_TOOL",
            _ =>
            {
                blockedWasInvoked = true;
                return Task.FromResult("unexpected");
            },
            "circuit-open",
            retrySafe: false,
            isolationKey: "tenant-a:shared-tool");
        var otherTenant = await policy.ExecuteWithResilienceAsync(
            "SHARED_TOOL",
            _ => Task.FromResult("tenant-b-ok"),
            "fallback",
            retrySafe: false,
            isolationKey: "tenant-b:shared-tool");

        Assert.False(blockedWasInvoked);
        Assert.Equal("circuit-open", blocked);
        Assert.Equal("tenant-b-ok", otherTenant);
    }

    [Fact]
    public void CircuitBreakerKey_IsStableAndDoesNotExposeTenantOrToolNames()
    {
        const string scope = "11111111-1111-1111-1111-111111111111:MCP:private-tool";

        var first = AgentResiliencePolicy.BuildCircuitKey(scope);
        var second = AgentResiliencePolicy.BuildCircuitKey(scope);

        Assert.Equal(first, second);
        Assert.Equal("LmKitOmniApi_cb:".Length + 24, first.Length);
        Assert.DoesNotContain("11111111", first);
        Assert.DoesNotContain("private-tool", first);
    }

    [SkippableFact]
    public async Task RedisCircuitBreaker_AtomicallyOpensAcrossPolicyInstances()
    {
        var connectionString = Environment.GetEnvironmentVariable("LMKIT_TEST_REDIS_CONNECTION");
        var redisConfigured = !string.IsNullOrWhiteSpace(connectionString);
        Skip.IfNot(redisConfigured, "LMKIT_TEST_REDIS_CONNECTION is not set; skipping the Redis-backed circuit breaker test.");

        using var redis = await ConnectionMultiplexer.ConnectAsync(connectionString!);
        var cache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        var isolationKey = $"redis-test:{Guid.NewGuid():N}";
        var redisKey = AgentResiliencePolicy.BuildCircuitKey(isolationKey);
        await redis.GetDatabase().KeyDeleteAsync(redisKey);
        try
        {
            var policies = Enumerable.Range(0, 5)
                .Select(_ => new AgentResiliencePolicy(NullLogger<AgentResiliencePolicy>.Instance, cache, redis))
                .ToArray();
            var releaseFailures = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var started = 0;
            var failures = policies.Select(policy => policy.ExecuteWithResilienceAsync(
                "DISTRIBUTED_TOOL",
                async _ =>
                {
                    Interlocked.Increment(ref started);
                    await releaseFailures.Task;
                    throw new InvalidOperationException("distributed failure");
                },
                "fallback",
                retrySafe: false,
                isolationKey: isolationKey)).ToArray();

            await WaitUntilAsync(() => Volatile.Read(ref started) == 5);
            releaseFailures.SetResult();
            await Task.WhenAll(failures);

            var invoked = false;
            var blocked = await policies[0].ExecuteWithResilienceAsync(
                "DISTRIBUTED_TOOL",
                _ =>
                {
                    invoked = true;
                    return Task.FromResult("unexpected");
                },
                "open",
                retrySafe: false,
                isolationKey: isolationKey);

            Assert.False(invoked);
            Assert.Equal("open", blocked);
            var database = redis.GetDatabase();
            Assert.True(await database.KeyTimeToLiveAsync(redisKey) > TimeSpan.Zero);

            await database.HashSetAsync(redisKey, "openedAt",
                DateTimeOffset.UtcNow.AddSeconds(-31).ToUnixTimeMilliseconds());
            var probeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseProbe = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var probe = policies[0].ExecuteWithResilienceAsync(
                "DISTRIBUTED_TOOL",
                async _ =>
                {
                    probeStarted.SetResult();
                    await releaseProbe.Task;
                    return "recovered";
                },
                "open",
                retrySafe: false,
                isolationKey: isolationKey);
            await probeStarted.Task;

            var secondProbeInvoked = false;
            var secondProbe = await policies[1].ExecuteWithResilienceAsync(
                "DISTRIBUTED_TOOL",
                _ =>
                {
                    secondProbeInvoked = true;
                    return Task.FromResult("unexpected");
                },
                "half-open-busy",
                retrySafe: false,
                isolationKey: isolationKey);
            releaseProbe.SetResult();

            Assert.False(secondProbeInvoked);
            Assert.Equal("half-open-busy", secondProbe);
            Assert.Equal("recovered", await probe);
            Assert.False(await database.KeyExistsAsync(redisKey));
        }
        finally
        {
            await redis.GetDatabase().KeyDeleteAsync(redisKey);
        }
    }

    [SkippableFact]
    public async Task RedisCircuitBreaker_FallsBackLocallyWhenRedisDisconnects()
    {
        var connectionString = Environment.GetEnvironmentVariable("LMKIT_TEST_REDIS_CONNECTION");
        var redisConfigured = !string.IsNullOrWhiteSpace(connectionString);
        Skip.IfNot(redisConfigured, "LMKIT_TEST_REDIS_CONNECTION is not set; skipping the Redis-backed circuit breaker test.");

        using var redis = await ConnectionMultiplexer.ConnectAsync(connectionString!);
        var cache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        var policy = new AgentResiliencePolicy(NullLogger<AgentResiliencePolicy>.Instance, cache, redis);
        var isolationKey = $"redis-disconnect:{Guid.NewGuid():N}";
        await redis.CloseAsync();

        for (var failure = 0; failure < 5; failure++)
        {
            var result = await policy.ExecuteWithResilienceAsync(
                "DISCONNECTED_TOOL",
                _ => throw new InvalidOperationException("dependency failed"),
                "fallback",
                retrySafe: false,
                isolationKey: isolationKey);
            Assert.Equal("fallback", result);
        }

        var invoked = false;
        var blocked = await policy.ExecuteWithResilienceAsync(
            "DISCONNECTED_TOOL",
            _ =>
            {
                invoked = true;
                return Task.FromResult("unexpected");
            },
            "open",
            retrySafe: false,
            isolationKey: isolationKey);

        Assert.False(invoked);
        Assert.Equal("open", blocked);
    }

    [Fact]
    public async Task ModelInferenceGate_QueuesAtConfiguredLimitAndHonorsCancellation()
    {
        using var manager = new LmModelManager(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SemaphoreLimits:Embedding"] = "1"
            })
            .Build());

        await using var firstLease = await manager.AcquireEmbeddingInferenceAsync();
        using var cancellation = new CancellationTokenSource();
        var secondAcquisition = manager.AcquireEmbeddingInferenceAsync(cancellation.Token).AsTask();

        await Task.Delay(50);
        Assert.False(secondAcquisition.IsCompleted);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => secondAcquisition);
    }

    [Fact]
    public void ModelInferenceGate_RejectsNonPositiveConfiguration()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SemaphoreLimits:Vision"] = "0"
            })
            .Build();

        var error = Assert.Throws<InvalidOperationException>(() => new LmModelManager(configuration));
        Assert.Contains("SemaphoreLimits:Vision", error.Message);
    }

    [Fact]
    public async Task ResiliencePolicy_PropagatesCallerCancellationInsteadOfReturningFallback()
    {
        var cache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        var policy = new AgentResiliencePolicy(NullLogger<AgentResiliencePolicy>.Instance, cache);
        using var cancellation = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => policy.ExecuteWithResilienceAsync(
            "CANCELLED_READ_TEST",
            _ =>
            {
                cancellation.Cancel();
                return Task.FromCanceled<string>(cancellation.Token);
            },
            "must-not-return",
            cancellation.Token));
    }

    [Fact]
    public void DistributedRateLimitPartition_IsStableAndDoesNotExposeUserId()
    {
        const string userId = "22222222-2222-2222-2222-222222222222";

        var first = DistributedAiRateLimitMiddleware.BuildPartitionHash(userId);
        var second = DistributedAiRateLimitMiddleware.BuildPartitionHash(userId);

        Assert.Equal(first, second);
        Assert.Equal(24, first.Length);
        Assert.DoesNotContain(userId, first, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LmKitReadiness_IsHealthyWhenModelReadinessIsOptional()
    {
        using var manager = new LmModelManager(new ConfigurationBuilder().Build());
        var check = new LmKitModelHealthCheck(manager, new ConfigurationBuilder().Build());

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public async Task LmKitReadiness_IsUnhealthyWhenRequiredLicenseIsMissing()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["LMKit:RequireLicense"] = "true"
            })
            .Build();
        using var manager = new LmModelManager(configuration);
        var check = new LmKitModelHealthCheck(manager, configuration);

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.DoesNotContain("LicenseKey", result.Description ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LmKitReadiness_IsUnhealthyUntilRequiredChatModelLoads()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AiModels:RequireChatModelReady"] = "true"
            })
            .Build();
        using var manager = new LmModelManager(configuration);
        var check = new LmKitModelHealthCheck(manager, configuration);

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("not loaded", result.Description ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
            await Task.Delay(10, timeout.Token);
    }
}
