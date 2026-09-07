using LmKitOmniApi.Infrastructure.Health;
using LmKitOmniApi.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;

namespace LmKitOmniApi.Tests;

/// <summary>
/// Honest readiness for the configured chat model, plus the load-once cache that stops two
/// roles pointing at one file from allocating the weights twice.
///
/// The defect: appsettings ships <c>AiModels:DefaultChat = "bonsai"</c> whose .gguf lives in
/// a gitignored directory with nothing committed and no download script. With
/// <c>WarmupChatModel=false</c> and <c>RequireChatModelReady=false</c> the process started
/// clean and <c>/health/ready</c> answered healthy, so the deployment looked fine right up
/// until the first user message failed. Readiness now reflects reality; liveness does not
/// change, because restarting the process cannot conjure a missing file.
/// </summary>
public sealed class ModelReadinessTests : IDisposable
{
    private readonly string _modelsDirectory = Path.Combine(
        Path.GetTempPath(), "lmkit-model-readiness-" + Guid.NewGuid().ToString("N"));

    public ModelReadinessTests() => Directory.CreateDirectory(_modelsDirectory);

    public void Dispose()
    {
        try { Directory.Delete(_modelsDirectory, recursive: true); }
        catch (IOException) { /* best effort */ }
    }

    [Fact]
    public async Task Readiness_IsUnhealthy_WhenTheConfiguredChatModelFileIsMissing()
    {
        var configuration = BuildConfiguration(chatModelId: "bonsai", createModelFile: false);
        using var manager = new LmModelManager(configuration);
        var check = new LmKitModelHealthCheck(manager, configuration);

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        // Actionable: names the config key AND the path an operator has to populate.
        Assert.Contains("AiModels:DefaultChat", result.Description!, StringComparison.Ordinal);
        Assert.Contains(Path.Combine(_modelsDirectory, "bonsai.gguf"), result.Description!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The old check short-circuited to Healthy whenever RequireChatModelReady was false —
    /// which is the shipped default, so the missing-model case never surfaced.
    /// </summary>
    [Fact]
    public async Task Readiness_IsUnhealthy_EvenWhenChatReadinessIsNotRequired()
    {
        var configuration = BuildConfiguration(
            chatModelId: "bonsai",
            createModelFile: false,
            extra: new Dictionary<string, string?> { ["AiModels:RequireChatModelReady"] = "false" });
        using var manager = new LmModelManager(configuration);
        var check = new LmKitModelHealthCheck(manager, configuration);

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }

    [Fact]
    public async Task Readiness_IsHealthy_WhenTheConfiguredChatModelExists()
    {
        var configuration = BuildConfiguration(chatModelId: "bonsai", createModelFile: true);
        using var manager = new LmModelManager(configuration);
        var check = new LmKitModelHealthCheck(manager, configuration);

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    /// <summary>
    /// A registered model that declares an mmproj projector needs BOTH files; the vision
    /// entry in appsettings does exactly that.
    /// </summary>
    [Fact]
    public async Task Readiness_IsUnhealthy_WhenTheMultimodalProjectorIsMissing()
    {
        File.WriteAllText(Path.Combine(_modelsDirectory, "bonsai.gguf"), string.Empty);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AiModels:ModelsDirectory"] = _modelsDirectory,
                ["AiModels:DefaultChat"] = "bonsai",
                ["AiModels:Models:bonsai:Path"] = "bonsai.gguf",
                ["AiModels:Models:bonsai:Mmproj"] = "bonsai-mmproj.gguf"
            })
            .Build();
        using var manager = new LmModelManager(configuration);
        var check = new LmKitModelHealthCheck(manager, configuration);

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("projector", result.Description!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A model id that is not in the local registry (an LM-Kit catalog id such as
    /// "qwen3.5:2b", or an https URL) can only be resolved by attempting a load, so it must
    /// NOT be reported as a missing file.
    /// </summary>
    [Fact]
    public async Task Readiness_DoesNotFalselyFail_ForUnregisteredModelIds()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AiModels:ModelsDirectory"] = _modelsDirectory,
                ["AiModels:DefaultChat"] = "qwen3.5:2b"
            })
            .Build();
        using var manager = new LmModelManager(configuration);
        var check = new LmKitModelHealthCheck(manager, configuration);

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public void MissingChatModel_IsLoggedAtStartup()
    {
        var logger = new RecordingLogger<LmModelManager>();

        using var manager = new LmModelManager(
            BuildConfiguration(chatModelId: "bonsai", createModelFile: false),
            logger);

        var critical = Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Critical);
        Assert.Contains("AiModels:DefaultChat", critical.Message, StringComparison.Ordinal);
        Assert.Contains("bonsai.gguf", critical.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PresentChatModel_IsSilentAtStartup()
    {
        var logger = new RecordingLogger<LmModelManager>();

        using var manager = new LmModelManager(
            BuildConfiguration(chatModelId: "bonsai", createModelFile: true),
            logger);

        Assert.DoesNotContain(logger.Entries, entry => entry.Level >= LogLevel.Error);
    }

    /// <summary>
    /// appsettings points DefaultEmbedding and DefaultReranker at the SAME registry entry
    /// ("bge-m3"), so the two roles must resolve to one cache key — otherwise the weights are
    /// loaded into memory twice.
    /// </summary>
    [Fact]
    public void EmbeddingAndReranker_OnTheSameFile_ShareOneCacheKey()
    {
        File.WriteAllText(Path.Combine(_modelsDirectory, "bge-m3.gguf"), string.Empty);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AiModels:ModelsDirectory"] = _modelsDirectory,
                ["AiModels:DefaultEmbedding"] = "bge-m3",
                ["AiModels:DefaultReranker"] = "bge-m3",
                ["AiModels:Models:bge-m3:Path"] = "bge-m3.gguf",
                ["AiModels:Models:vision:Path"] = "bge-m3.gguf",
                ["AiModels:Models:vision:Mmproj"] = "projector.gguf"
            })
            .Build();
        using var manager = new LmModelManager(configuration);

        var embedding = manager.ResolveRegisteredModelForTests(manager.DefaultEmbeddingModelId)!;
        var reranker = manager.ResolveRegisteredModelForTests(manager.DefaultRerankerModelId)!;
        var withProjector = manager.ResolveRegisteredModelForTests("vision")!;

        Assert.Equal(
            LmModelManager.BuildSharedModelKey(embedding),
            LmModelManager.BuildSharedModelKey(reranker));
        // Same weights file, different companion projector → must NOT alias.
        Assert.NotEqual(
            LmModelManager.BuildSharedModelKey(embedding),
            LmModelManager.BuildSharedModelKey(withProjector));
        Assert.Equal(0, manager.LoadedModelCount);
    }

    private IConfiguration BuildConfiguration(
        string chatModelId,
        bool createModelFile,
        IDictionary<string, string?>? extra = null)
    {
        if (createModelFile)
            File.WriteAllText(Path.Combine(_modelsDirectory, chatModelId + ".gguf"), string.Empty);

        var settings = new Dictionary<string, string?>
        {
            ["AiModels:ModelsDirectory"] = _modelsDirectory,
            ["AiModels:DefaultChat"] = chatModelId,
            [$"AiModels:Models:{chatModelId}:Path"] = chatModelId + ".gguf"
        };

        if (extra is not null)
        {
            foreach (var pair in extra) settings[pair.Key] = pair.Value;
        }

        return new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
    }

    private sealed record LogEntry(LogLevel Level, string Message);

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<LogEntry> Entries { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add(new LogEntry(logLevel, formatter(state, exception)));

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}

/// <summary>
/// The load-once cache behind the duplicate-model fix. Exercised directly because loading a
/// real LM needs multi-gigabyte weights that are not (and should not be) in the repo.
/// </summary>
public sealed class SharedInstanceCacheTests
{
    [Fact]
    public async Task SameKey_LoadsOnce_AndHandsBackTheSameInstance()
    {
        var cache = new SharedInstanceCache<object>();
        var loads = 0;

        var first = await cache.GetOrLoadAsync("model-a", _ => { loads++; return Task.FromResult(new object()); }, default);
        var second = await cache.GetOrLoadAsync("model-a", _ => { loads++; return Task.FromResult(new object()); }, default);

        Assert.Same(first, second);
        Assert.Equal(1, loads);
        Assert.Single(cache.LoadedValues);
    }

    [Fact]
    public async Task ConcurrentCallers_ShareOneInFlightLoad()
    {
        var cache = new SharedInstanceCache<object>();
        var loads = 0;
        var gate = new TaskCompletionSource();

        async Task<object> SlowLoad(CancellationToken _)
        {
            Interlocked.Increment(ref loads);
            await gate.Task;
            return new object();
        }

        var first = cache.GetOrLoadAsync("model-a", SlowLoad, default);
        var second = cache.GetOrLoadAsync("model-a", SlowLoad, default);
        gate.SetResult();

        Assert.Same(await first, await second);
        Assert.Equal(1, loads);
    }

    [Fact]
    public async Task DifferentKeys_LoadIndependently()
    {
        var cache = new SharedInstanceCache<object>();

        var first = await cache.GetOrLoadAsync("model-a", _ => Task.FromResult(new object()), default);
        var second = await cache.GetOrLoadAsync("model-b", _ => Task.FromResult(new object()), default);

        Assert.NotSame(first, second);
        Assert.Equal(2, cache.LoadedValues.Count);
    }

    [Fact]
    public async Task FailedLoad_IsEvicted_SoTheNextCallerCanRetry()
    {
        var cache = new SharedInstanceCache<object>();

        await Assert.ThrowsAsync<FileNotFoundException>(() => cache.GetOrLoadAsync(
            "model-a",
            _ => Task.FromException<object>(new FileNotFoundException("weights missing")),
            default));

        var retried = await cache.GetOrLoadAsync("model-a", _ => Task.FromResult(new object()), default);

        Assert.NotNull(retried);
        Assert.Single(cache.LoadedValues);
    }
}
