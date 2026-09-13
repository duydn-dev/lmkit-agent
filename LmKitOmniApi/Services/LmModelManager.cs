using LMKit.Model;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;

namespace LmKitOmniApi.Services;

public class LmModelManager : IDisposable
{
    private LM? _chatModel;
    private LM? _visionModel;
    private LM? _embeddingModel;
    private LM? _speechModel;
    private LM? _rerankerModel;
    private LM? _segmentationModel;

    // M2 Fix: Per-model locks to prevent cross-model blocking.
    // Previously a single SemaphoreSlim(1,1) blocked ALL model loads — if chat model
    // took 30s to load, embedding/vision/reranker requests were all queued behind it.
    private readonly SemaphoreSlim _chatLock;
    private readonly SemaphoreSlim _visionLock;
    private readonly SemaphoreSlim _embeddingLock;
    private readonly SemaphoreSlim _speechLock;
    private readonly SemaphoreSlim _rerankerLock;
    private readonly SemaphoreSlim _segmentationLock;
    private readonly SemaphoreSlim _chatInferenceGate;
    private readonly SemaphoreSlim _visionInferenceGate;
    private readonly SemaphoreSlim _embeddingInferenceGate;
    private readonly SemaphoreSlim _speechInferenceGate;
    private readonly SemaphoreSlim _rerankerInferenceGate;
    private readonly SemaphoreSlim _segmentationInferenceGate;

    // The chat gate is the one every conversation, agent run, approved-tool resume and
    // voice turn passes through, and SemaphoreLimits:Chat is 1. Waiting on it directly
    // (`gate.WaitAsync(ct)`) is unbounded, unmeasured and silent; the queue puts a bound,
    // metrics and a progress signal in front of it WITHOUT changing the permit primitive
    // or the release discipline. Other gates keep the plain wait.
    private readonly InferenceAdmissionQueue _chatQueue;
    private readonly long _maxDownloadBytes;
    private readonly TimeSpan _downloadTimeout;
    private readonly string _modelsDirectory;
    private readonly ILogger<LmModelManager> _logger;

    // LM-Kit catalog IDs are the normal configuration. The optional legacy registry is still
    // understood for compatibility with existing test/deployment overrides, but appsettings
    // does not need an AiModels:Models block.
    private readonly IReadOnlyDictionary<string, RegisteredModel> _registeredModels;

    // One loaded LM per RESOLVED model file, shared across roles.
    private readonly SharedInstanceCache<LM> _sharedModels = new();

    public string DefaultChatModelId { get; set; }
    public string DefaultVisionModelId { get; set; }
    public string DefaultEmbeddingModelId { get; set; }
    public string DefaultSpeechModelId { get; set; }
    public string DefaultRerankerModelId { get; set; }
    public string DefaultSegmentationModelId { get; set; }
    public bool IsChatModelLoaded => _chatModel is not null;
    public string? LastChatModelLoadError { get; private set; }

    public LmModelManager(
        Microsoft.Extensions.Configuration.IConfiguration configuration,
        ILogger<LmModelManager>? logger = null)
    {
        _logger = logger ?? NullLogger<LmModelManager>.Instance;
        var config = configuration.GetSection("AiModels");
        DefaultChatModelId = config["DefaultChat"] ?? "gemma4:e4b";
        DefaultVisionModelId = config["DefaultVision"] ?? "glm-ocr";
        DefaultEmbeddingModelId = config["DefaultEmbedding"] ?? "bge-m3";
        DefaultSpeechModelId = config["DefaultSpeech"] ?? "whisper-tiny";
        DefaultRerankerModelId = config["DefaultReranker"] ?? "bge-m3-reranker";
        DefaultSegmentationModelId = config["DefaultSegmentation"] ?? "u2net";
        _maxDownloadBytes = config.GetValue<long>("MaxDownloadBytes", 8L * 1024 * 1024 * 1024);
        if (_maxDownloadBytes <= 0)
            throw new InvalidOperationException("AiModels:MaxDownloadBytes must be greater than zero.");
        var timeoutMinutes = config.GetValue<int>("DownloadTimeoutMinutes", 30);
        if (timeoutMinutes is < 1 or > 180)
            throw new InvalidOperationException("AiModels:DownloadTimeoutMinutes must be between 1 and 180.");
        _downloadTimeout = TimeSpan.FromMinutes(timeoutMinutes);

        _modelsDirectory = ResolveModelsDirectory(config["ModelsDirectory"]);
        _registeredModels = ParseRegisteredModels(_modelsDirectory, config.GetSection("Models"));

        var limits = configuration.GetSection("SemaphoreLimits");
        var chatLimit = GetPositiveLimit(limits, "Chat", 1);
        var visionLimit = GetPositiveLimit(limits, "Vision", 1);
        var embeddingLimit = GetPositiveLimit(limits, "Embedding", 1);
        var speechLimit = GetPositiveLimit(limits, "Speech", 1);
        var rerankerLimit = GetPositiveLimit(limits, "Reranker", 1);
        var segmentationLimit = GetPositiveLimit(limits, "Segmentation", 1);
        _chatLock = new SemaphoreSlim(1, 1);
        _visionLock = new SemaphoreSlim(1, 1);
        _embeddingLock = new SemaphoreSlim(1, 1);
        _speechLock = new SemaphoreSlim(1, 1);
        _rerankerLock = new SemaphoreSlim(1, 1);
        _segmentationLock = new SemaphoreSlim(1, 1);
        _chatInferenceGate = new SemaphoreSlim(chatLimit, chatLimit);
        _visionInferenceGate = new SemaphoreSlim(visionLimit, visionLimit);
        _embeddingInferenceGate = new SemaphoreSlim(embeddingLimit, embeddingLimit);
        _speechInferenceGate = new SemaphoreSlim(speechLimit, speechLimit);
        _rerankerInferenceGate = new SemaphoreSlim(rerankerLimit, rerankerLimit);
        _segmentationInferenceGate = new SemaphoreSlim(segmentationLimit, segmentationLimit);
        _chatQueue = new InferenceAdmissionQueue(
            _chatInferenceGate,
            "chat",
            InferenceQueueOptions.FromConfiguration(configuration, "InferenceQueue:Chat"),
            _logger);

        LogDefaultModelAvailability();
    }

    /// <summary>
    /// Startup audit: a registered default whose weights file is absent CANNOT serve a
    /// single request, so say so loudly at boot instead of letting the first user message
    /// discover it. The chat model is escalated to Critical because it is the one every
    /// conversation needs; <see cref="Infrastructure.Health.LmKitModelHealthCheck"/> turns
    /// the same fact into an unhealthy <c>/health/ready</c>.
    /// </summary>
    private void LogDefaultModelAvailability()
    {
        var chatProblem = DescribeDefaultModelProblem("AiModels:DefaultChat", DefaultChatModelId);
        if (chatProblem is not null)
            _logger.LogCritical("{Problem}", chatProblem);

        foreach (var (configKey, modelId) in new[]
        {
            ("AiModels:DefaultVision", DefaultVisionModelId),
            ("AiModels:DefaultEmbedding", DefaultEmbeddingModelId),
            ("AiModels:DefaultSpeech", DefaultSpeechModelId),
            ("AiModels:DefaultReranker", DefaultRerankerModelId),
            ("AiModels:DefaultSegmentation", DefaultSegmentationModelId)
        })
        {
            var problem = DescribeDefaultModelProblem(configKey, modelId);
            if (problem is not null) _logger.LogError("{Problem}", problem);
        }
    }

    /// <summary>
    /// Null when the configured chat model is usable (or cannot be judged without a load);
    /// otherwise an operator-actionable sentence naming the config key and the missing file.
    /// </summary>
    public string? DescribeDefaultChatModelProblem()
    {
        if (LastChatModelLoadError is { Length: > 0 } loadError)
            return $"The chat model configured by AiModels:DefaultChat ('{DefaultChatModelId}') failed to load: {loadError}";

        return DescribeDefaultModelProblem("AiModels:DefaultChat", DefaultChatModelId);
    }

    /// <summary>
    /// Static resolvability check for one configured default. A direct local path is checked
    /// against the filesystem; a bare LM-Kit catalog id or an https URL resolves at load time.
    /// </summary>
    private string? DescribeDefaultModelProblem(string configKey, string modelId)
    {
        var registered = ResolveRegisteredModel(modelId);
        if (registered is not null)
        {
            if (!File.Exists(registered.ResolvedModelPath))
            {
                return $"Model '{registered.Key}' (configured by {configKey}) has no weights file at " +
                    $"'{registered.ResolvedModelPath}'. Place the file there, or point {configKey} " +
                    "at a model path that exists.";
            }

            if (registered.ResolvedMmprojPath is not null && !File.Exists(registered.ResolvedMmprojPath))
            {
                return $"Model '{registered.Key}' (configured by {configKey}) is missing its multimodal " +
                    $"projector at '{registered.ResolvedMmprojPath}'. Place the file there, or clear " +
                    $"AiModels:Models:{registered.Key}:Mmproj.";
            }

            return null;
        }

        // Direct local paths are the preferred configuration. Unlike a catalog id such as
        // "qwen3.5:4b", a path is statically checkable before the first request.
        var remoteModelPath = ResolveLocalModelPath(modelId);
        if (remoteModelPath is not null && !File.Exists(remoteModelPath))
        {
            return $"Model configured by {configKey} has no weights file at '{remoteModelPath}'. " +
                $"Place the model there or point {configKey} at an existing LM-Kit model path.";
        }

        return null;
    }

    /// <summary>Default folder (relative to the working directory) holding local model files.</summary>
    internal const string DefaultModelsDirectoryName = "AIModels";

    /// <summary>
    /// A model declared in AiModels:Models. Paths are pre-resolved to absolute locations;
    /// a non-null ResolvedMmprojPath marks a two-file vision model (GGUF + mmproj projector).
    /// </summary>
    internal sealed record RegisteredModel(string Key, string ResolvedModelPath, string? ResolvedMmprojPath);

    internal static string ResolveModelsDirectory(string? configured)
    {
        var directory = string.IsNullOrWhiteSpace(configured) ? DefaultModelsDirectoryName : configured!;
        return Path.IsPathRooted(directory)
            ? directory
            : Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), directory));
    }

    internal RegisteredModel? ResolveRegisteredModel(string? modelId) =>
        modelId is not null
        && _registeredModels.TryGetValue(modelId.Trim(), out var registered)
            ? registered
            : null;

    private static IReadOnlyDictionary<string, RegisteredModel> ParseRegisteredModels(
        string modelsDirectory,
        IConfigurationSection section)
    {
        var models = new Dictionary<string, RegisteredModel>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in section.GetChildren())
        {
            var key = entry.Key.Trim();
            if (key.Length == 0)
                throw new InvalidOperationException("AiModels:Models entries must use a non-empty key.");
            var modelPath = entry["Path"];
            if (string.IsNullOrWhiteSpace(modelPath))
                throw new InvalidOperationException($"AiModels:Models:{key}:Path must be set when the entry '{key}' is declared.");
            var mmprojPath = entry["Mmproj"];
            models[key] = new RegisteredModel(
                key,
                ResolveConfiguredPath(modelsDirectory, key, "Path", modelPath),
                string.IsNullOrWhiteSpace(mmprojPath)
                    ? null
                    : ResolveConfiguredPath(modelsDirectory, key, "Mmproj", mmprojPath));
        }
        return models;
    }

    /// <summary>Test seam: <see cref="ParseRegisteredModels" /> is private.</summary>
    internal static IReadOnlyDictionary<string, RegisteredModel> ParseRegisteredModelsForTests(
        string modelsDirectory,
        IConfigurationSection section) => ParseRegisteredModels(modelsDirectory, section);

    /// <summary>Test seam: exposes registry lookup on a built manager instance.</summary>
    internal RegisteredModel? ResolveRegisteredModelForTests(string? modelId) => ResolveRegisteredModel(modelId);

    /// <summary>
    /// Resolves an explicit local model path for compatibility without requiring a registry entry.
    /// Catalog IDs (for example <c>qwen3.5:4b</c>) return null and are passed to LM-Kit with
    /// <c>storagePath: _modelsDirectory</c>, allowing the catalog to resolve/download them.
    /// </summary>
    internal string? ResolveLocalModelPath(string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId)) return null;
        var candidate = modelId.Trim();

        if (Uri.TryCreate(candidate, UriKind.Absolute, out var uri)
            && !uri.IsFile)
            return null;

        if (uri?.IsFile == true)
            return Path.GetFullPath(uri.LocalPath);

        var workingDirectoryPath = Path.GetFullPath(candidate);
        if (File.Exists(workingDirectoryPath)) return workingDirectoryPath;

        var modelsDirectoryPath = Path.GetFullPath(Path.Combine(_modelsDirectory, candidate));
        if (File.Exists(modelsDirectoryPath)) return modelsDirectoryPath;

        // When the configured models directory is itself the application-relative AIModels
        // folder, a value such as "AIModels/Qwen...lmk" would otherwise become
        // "AIModels/AIModels/Qwen...lmk" on the second lookup. Accept both the repo-root
        // form and the models-directory-relative form.
        var modelsDirectoryName = Path.GetFileName(
            _modelsDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (!string.IsNullOrWhiteSpace(modelsDirectoryName)
            && (candidate.StartsWith(modelsDirectoryName + "/", StringComparison.OrdinalIgnoreCase)
                || candidate.StartsWith(modelsDirectoryName + "\\", StringComparison.OrdinalIgnoreCase)))
        {
            var relativeModelName = candidate[(modelsDirectoryName.Length + 1)..];
            var prefixedPath = Path.GetFullPath(Path.Combine(_modelsDirectory, relativeModelName));
            if (File.Exists(prefixedPath)) return prefixedPath;
        }

        // A missing explicit path must still be reported by readiness. Catalog ids use the
        // LM-Kit convention (qwen3.5:4b) and intentionally do not enter this branch.
        var looksLikePath = Path.IsPathRooted(candidate)
            || candidate.Contains('/')
            || candidate.Contains('\\')
            || candidate.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase)
            || candidate.EndsWith(".lmk", StringComparison.OrdinalIgnoreCase)
            || candidate.EndsWith(".bin", StringComparison.OrdinalIgnoreCase);
        if (!looksLikePath) return null;

        return workingDirectoryPath;
    }

    /// <summary>Test seam for the direct path resolver.</summary>
    internal string? ResolveLocalModelPathForTests(string? modelId) => ResolveLocalModelPath(modelId);

    private static string ResolveConfiguredPath(string modelsDirectory, string entryKey, string settingName, string configuredPath)
    {
        if (Path.IsPathRooted(configuredPath))
            return configuredPath;
        if (configuredPath.Split('/', '\\').Contains(".."))
            throw new InvalidOperationException(
                $"AiModels:Models:{entryKey}:{settingName} must not traverse outside the models directory ('{configuredPath}').");
        return Path.GetFullPath(Path.Combine(modelsDirectory, configuredPath));
    }


    private async Task<LM> LoadRegisteredModelAsync(RegisteredModel registered, CancellationToken ct)
    {
        _logger.LogInformation(
            "Loading registered model {ModelKey} from {ModelPath}{ProjectorInfo}",
            registered.Key,
            registered.ResolvedModelPath,
            registered.ResolvedMmprojPath is null ? string.Empty : $" with projector {registered.ResolvedMmprojPath}");

        return await Task.Run(
            () => registered.ResolvedMmprojPath is null
                ? new LM(registered.ResolvedModelPath)
                : new LM(new Uri(registered.ResolvedModelPath), new Uri(registered.ResolvedMmprojPath)),
            ct);
    }

    private static int GetPositiveLimit(IConfigurationSection section, string name, int fallback)
    {
        var value = section.GetValue<int>(name, fallback);
        if (value <= 0)
            throw new InvalidOperationException($"SemaphoreLimits:{name} must be greater than zero.");
        return value;
    }
    /// <summary>
    /// Cache key for one loaded LM. Roles that resolve to the same file share one instance;
    /// </summary>
    internal static string BuildSharedModelKey(RegisteredModel registered) =>
        registered.ResolvedMmprojPath is null
            ? registered.ResolvedModelPath
            : $"{registered.ResolvedModelPath}|{registered.ResolvedMmprojPath}";

    private async Task<LM> LoadModelWithProgressAsync(string id, CancellationToken ct = default)
    {
        var registered = ResolveRegisteredModel(id);
        if (registered is not null)
        {
            return await _sharedModels.GetOrLoadAsync(
                BuildSharedModelKey(registered),
                token => LoadRegisteredModelAsync(registered, token),
                ct);
        }

        var downloadedPath = ResolveLocalModelPath(id);
        if (downloadedPath is not null)
        {
            if (!File.Exists(downloadedPath))
                throw new FileNotFoundException($"Local model file was not found: {downloadedPath}", downloadedPath);

            return await _sharedModels.GetOrLoadAsync(
                downloadedPath,
                token => Task.Run(() => new LM(downloadedPath), token),
                ct);
        }

        if (id.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || id.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            // Tự động chuyển link /blob/ sang /resolve/ của HuggingFace để lấy file RAW
            if (id.Contains("huggingface.co") && id.Contains("/blob/"))
            {
                id = id.Replace("/blob/", "/resolve/");
            }

            var sourceUri = new Uri(id, UriKind.Absolute);
            await ValidateRemoteModelUriAsync(sourceUri, ct);
            var fileName = Path.GetFileName(sourceUri.LocalPath);
            if (string.IsNullOrEmpty(fileName)) fileName = "model.gguf";
            fileName = string.Concat(fileName.Select(character =>
                Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));
            
            var modelsDir = _modelsDirectory;
            Directory.CreateDirectory(modelsDir);
            var remoteModelPath = Path.Combine(modelsDir, fileName);

            if (!File.Exists(remoteModelPath))
            {
                _logger.LogInformation("Downloading configured model from {ModelUri}", sourceUri);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(_downloadTimeout);
                using var handler = new SocketsHttpHandler { AllowAutoRedirect = false };
                using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
                using var response = await SendWithValidatedRedirectsAsync(client, sourceUri, timeout.Token);

                var totalBytes = response.Content.Headers.ContentLength ?? -1L;
                if (totalBytes > _maxDownloadBytes)
                    throw new InvalidOperationException($"Configured model exceeds the {_maxDownloadBytes} byte download limit.");
                var canReportProgress = totalBytes != -1 && totalBytes != 0;

                await using var contentStream = await response.Content.ReadAsStreamAsync(timeout.Token);
                var temporaryPath = remoteModelPath + $".{Guid.NewGuid():N}.download";

                try
                {
                    await using (var fileStream = new FileStream(
                        temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, true))
                    {
                        var buffer = new byte[64 * 1024];
                        var totalRead = 0L;
                        var lastProgress = -1;
                        int bytesRead;
                        while ((bytesRead = await contentStream.ReadAsync(buffer, timeout.Token)) != 0)
                        {
                            totalRead += bytesRead;
                            if (totalRead > _maxDownloadBytes)
                                throw new InvalidOperationException($"Configured model exceeded the {_maxDownloadBytes} byte download limit.");

                            await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), timeout.Token);
                            if (canReportProgress)
                            {
                                var progress = (int)((totalRead * 100) / totalBytes);
                                if (progress >= lastProgress + 5)
                                {
                                    _logger.LogInformation("Model download progress: {Progress}%", progress);
                                    lastProgress = progress;
                                }
                            }
                        }

                        await fileStream.FlushAsync(timeout.Token);
                    }

                    File.Move(temporaryPath, remoteModelPath);
                }
                finally
                {
                    if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
                }
                _logger.LogInformation("Configured model download completed at {LocalPath}", remoteModelPath);
            }
            else
            {
                _logger.LogInformation("Using existing configured model at {LocalPath}", remoteModelPath);
            }
            
            id = remoteModelPath; // Keep downloaded artifacts under the configured AIModels directory.
        }

        var resolvedId = id;
        var cachedCatalogFile = FindCachedCatalogModelFile(resolvedId);
        if (cachedCatalogFile is not null)
        {
            // The catalog weights are already on disk. Loading the file directly skips
            // LM-Kit's remote origin check inside LoadFromModelID, which fails hard
            // (HTTP 416 Range Not Satisfiable) when its resume bookkeeping disagrees
            // with a locally-seeded cache — leaving chat unservable with the model
            // physically present. Cache-first, catalog-download only when missing.
            return await _sharedModels.GetOrLoadAsync(
                cachedCatalogFile,
                token => Task.Run(
                    () =>
                    {
                        _logger.LogInformation(
                            "Loading cached catalog model {ModelId} from {ModelPath}",
                            resolvedId,
                            cachedCatalogFile);
                        return new LM(cachedCatalogFile);
                    },
                    token),
                ct);
        }

        return await _sharedModels.GetOrLoadAsync(
            resolvedId,
            async token =>
            {
                _logger.LogInformation("Loading model {ModelId} from {ModelsDirectory}", resolvedId, _modelsDirectory);
                var model = await Task.Run(
                    () => LM.LoadFromModelID(resolvedId, storagePath: _modelsDirectory),
                    token);
                _logger.LogInformation("Model loaded successfully");
                return model;
            },
            ct);
    }

    /// <summary>
    /// Locates an already-downloaded catalog model inside <see cref="_modelsDirectory"/>.
    ///
    /// <para>
    /// LM-Kit caches a catalog download under a per-model subdirectory named after the
    /// HF repository — NOT the model ID — e.g. id <c>qwen3.5:4b</c> lands in
    /// <c>qwen3.5-4b-lmk/</c>. Older layouts kept weights flat in the root. Matching on
    /// <c>Path.Combine(dir, modelId)</c> alone therefore never hit, the loader fell through
    /// to <c>LoadFromModelID</c>, and a flat-root file belonging to a DIFFERENT model
    /// (GLM-OCR) could be picked up instead — an OCR model served as the chat model.
    /// </para>
    /// <para>
    /// Matching is by normalized ID tokens against the subdirectory name, and the base
    /// weights are chosen over the vision projector (<c>mmproj-*</c>) or the
    /// <c>.origin</c> marker. Returns null when nothing matches so the caller falls back
    /// to a real catalog download.
    /// </para>
    /// </summary>
    private string? FindCachedCatalogModelFile(string modelId)
    {
        try
        {
            if (!Directory.Exists(_modelsDirectory))
                return null;

            // Compare on the alphanumeric-only form so separator differences between the
            // catalog ID and the HF repository name never break the match:
            //   id  "gemma4:e4b"  -> "gemma4e4b"
            //   dir "gemma-4-e4b-instruct-lmk" -> "gemma4e4binstructlmk"  (contains)
            var idKey = ToMatchKey(modelId);
            if (idKey.Length < 3)
                return null;

            // 1) LM-Kit's per-repository subdirectory (the normal case).
            foreach (var directory in Directory.EnumerateDirectories(_modelsDirectory))
            {
                if (!ToMatchKey(Path.GetFileName(directory)).Contains(idKey, StringComparison.OrdinalIgnoreCase))
                    continue;

                var cached = SelectBaseWeights(directory);
                if (cached is not null)
                    return cached;
            }

            // 2) Legacy flat layout: only a file whose own name carries the model ID.
            return Directory
                .EnumerateFiles(_modelsDirectory, "*.*", SearchOption.TopDirectoryOnly)
                .FirstOrDefault(file =>
                    ToMatchKey(Path.GetFileName(file)).Contains(idKey, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "Catalog cache scan failed for {ModelId}; falling back to LoadFromModelID",
                modelId);
        }

        return null;
    }

    /// <summary>
    /// Lower-cases and strips every separator so a catalog ID and the corresponding HF
    /// repository directory compare equal regardless of punctuation.
    /// </summary>
    private static string ToMatchKey(string value) =>
        new string(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    /// <summary>
    /// Picks the base weights out of one cached model directory: the largest model file,
    /// never the vision projector (<c>mmproj-*</c>), the <c>.origin</c> download marker
    /// or a partial <c>.download</c>.
    /// </summary>
    private static string? SelectBaseWeights(string directory)
    {
        return Directory
            .EnumerateFiles(directory, "*.*", SearchOption.TopDirectoryOnly)
            .Where(file =>
            {
                var name = Path.GetFileName(file);
                if (name.StartsWith("mmproj-", StringComparison.OrdinalIgnoreCase)) return false;
                if (name.EndsWith(".origin", StringComparison.OrdinalIgnoreCase)) return false;
                if (name.EndsWith(".download", StringComparison.OrdinalIgnoreCase)) return false;

                var extension = Path.GetExtension(file);
                return extension.Equals(".lmk", StringComparison.OrdinalIgnoreCase)
                    || extension.Equals(".gguf", StringComparison.OrdinalIgnoreCase);
            })
            .OrderByDescending(file => new FileInfo(file).Length)
            .FirstOrDefault();
    }

    private static async Task<HttpResponseMessage> SendWithValidatedRedirectsAsync(
        HttpClient client,
        Uri initialUri,
        CancellationToken ct)
    {
        var currentUri = initialUri;
        for (var redirectCount = 0; redirectCount <= 5; redirectCount++)
        {
            await ValidateRemoteModelUriAsync(currentUri, ct);
            var response = await client.GetAsync(currentUri, HttpCompletionOption.ResponseHeadersRead, ct);
            if ((int)response.StatusCode is >= 300 and < 400)
            {
                var location = response.Headers.Location;
                response.Dispose();
                if (location is null)
                    throw new InvalidOperationException("Model download redirect did not include a destination.");
                currentUri = location.IsAbsoluteUri ? location : new Uri(currentUri, location);
                continue;
            }

            response.EnsureSuccessStatusCode();
            return response;
        }

        throw new InvalidOperationException("Model download exceeded the redirect limit.");
    }

    private static async Task ValidateRemoteModelUriAsync(Uri uri, CancellationToken ct = default)
    {
        if (!uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Remote models must use HTTPS.");
        if (!IsTrustedModelHost(uri.DnsSafeHost))
            throw new InvalidOperationException("Remote model host is not trusted.");

        var addresses = await Dns.GetHostAddressesAsync(uri.DnsSafeHost, ct);
        if (addresses.Length == 0 || addresses.Any(IsPrivateOrLocalAddress))
            throw new InvalidOperationException("Remote model host resolved to a private or local address.");
    }

    internal static bool IsTrustedModelHost(string host) =>
        host.Equals("huggingface.co", StringComparison.OrdinalIgnoreCase)
        || host.EndsWith(".huggingface.co", StringComparison.OrdinalIgnoreCase)
        || host.Equals("hf.co", StringComparison.OrdinalIgnoreCase)
        || host.EndsWith(".hf.co", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Forwards to the single authoritative
    /// <see cref="Infrastructure.Security.PrivateNetworkClassifier"/>. This used to be a
    /// near-duplicate of the copy in ToolSandboxService — the two drifted apart (this one
    /// blocked <c>::</c>, the shared one did not; neither blocked IPv6 ULA), which is exactly
    /// the failure mode a second copy invites.
    /// </summary>
    internal static bool IsPrivateOrLocalAddress(IPAddress address)
        => Infrastructure.Security.PrivateNetworkClassifier.IsPrivateOrLocal(address);

    public async Task<LM> GetChatModelAsync(string? modelId = null, CancellationToken ct = default)
    {
        if (_chatModel != null) return _chatModel;
        await _chatLock.WaitAsync(ct);
        try
        {
            if (_chatModel == null)
            {
                var id = modelId ?? DefaultChatModelId;
                try
                {
                    _chatModel = await LoadModelWithProgressAsync(id, ct);
                    LastChatModelLoadError = null;
                }
                catch (Exception ex)
                {
                    // Keep the message, not just the type name: "FileNotFoundException" alone
                    // tells an operator nothing, while the message names the path that is missing.
                    LastChatModelLoadError = $"{ex.GetType().Name}: {ex.Message}";
                    _logger.LogCritical(
                        ex,
                        "The chat model configured by AiModels:DefaultChat ('{ModelId}') failed to load; chat requests cannot be served.",
                        id);
                    throw;
                }
            }
            return _chatModel;
        }
        finally
        {
            _chatLock.Release();
        }
    }

    /// <summary>
    /// Drop-in replacement for the previous direct semaphore wait: still returns a lease the
    /// caller MUST dispose, still throws <see cref="OperationCanceledException"/> when the
    /// caller's own token fires, and still completes SYNCHRONOUSLY when the permit is free —
    /// the uncontended single-user path allocates no timer and takes no queue bookkeeping.
    /// What is new is the bound: a wait that exceeds <c>InferenceQueue:Chat:MaxWaitSeconds</c>,
    /// or that arrives behind more than <c>MaxQueueDepth</c> others, now raises
    /// <see cref="InferenceQueueRejectedException"/> instead of hanging indefinitely.
    /// </summary>
    public ValueTask<IAsyncDisposable> AcquireChatInferenceAsync(CancellationToken ct = default)
        => _chatQueue.AcquireAsync(ct);

    /// <summary>
    /// Streaming variant of <see cref="AcquireChatInferenceAsync"/>: <c>await using</c> the
    /// returned admission, then enumerate <see cref="InferenceAdmission.WaitForTurnAsync"/>
    /// and forward what it yields. It yields nothing when the permit is free, and
    /// <c>[THINKING]:</c> queue-position notices once the wait becomes user-visible.
    /// </summary>
    public InferenceAdmission BeginChatInference(CancellationToken ct = default)
        => _chatQueue.Begin(ct);

    /// <summary>Callers currently queued for the chat permit (holders excluded).</summary>
    public int ChatQueueDepth => _chatQueue.QueueDepth;

    /// <summary>Test/diagnostics seam over the chat queue's in-process counters.</summary>
    public InferenceQueueSnapshot GetChatQueueSnapshot() => _chatQueue.GetSnapshot();

    internal InferenceAdmissionQueue ChatQueueForTests => _chatQueue;

    public async ValueTask<IAsyncDisposable> AcquireVisionInferenceAsync(CancellationToken ct = default)
        => await AcquireInferenceAsync(_visionInferenceGate, ct);

    public async ValueTask<IAsyncDisposable> AcquireEmbeddingInferenceAsync(CancellationToken ct = default)
        => await AcquireInferenceAsync(_embeddingInferenceGate, ct);

    public async ValueTask<IAsyncDisposable> AcquireSpeechInferenceAsync(CancellationToken ct = default)
        => await AcquireInferenceAsync(_speechInferenceGate, ct);

    public async ValueTask<IAsyncDisposable> AcquireRerankerInferenceAsync(CancellationToken ct = default)
        => await AcquireInferenceAsync(_rerankerInferenceGate, ct);

    public async ValueTask<IAsyncDisposable> AcquireSegmentationInferenceAsync(CancellationToken ct = default)
        => await AcquireInferenceAsync(_segmentationInferenceGate, ct);

    private static async ValueTask<IAsyncDisposable> AcquireInferenceAsync(
        SemaphoreSlim gate,
        CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        return new SemaphoreLease(gate);
    }

    public async Task<LM> GetVisionModelAsync(string? modelId = null, CancellationToken ct = default)
    {
        if (_visionModel != null) return _visionModel;
        await _visionLock.WaitAsync(ct);
        try
        {
            if (_visionModel == null)
            {
                var id = modelId ?? DefaultVisionModelId;
                _visionModel = await LoadModelWithProgressAsync(id, ct);
            }
            return _visionModel;
        }
        finally
        {
            _visionLock.Release();
        }
    }

    public async Task<LM> GetEmbeddingModelAsync(string? modelId = null, CancellationToken ct = default)
    {
        if (_embeddingModel != null) return _embeddingModel;
        await _embeddingLock.WaitAsync(ct);
        try
        {
            if (_embeddingModel == null)
            {
                var id = modelId ?? DefaultEmbeddingModelId;
                _embeddingModel = await LoadModelWithProgressAsync(id, ct);
            }
            return _embeddingModel;
        }
        finally
        {
            _embeddingLock.Release();
        }
    }

    public async Task<LM> GetRerankerModelAsync(string? modelId = null, CancellationToken ct = default)
    {
        if (_rerankerModel != null) return _rerankerModel;
        await _rerankerLock.WaitAsync(ct);
        try
        {
            if (_rerankerModel == null)
            {
                var id = modelId ?? DefaultRerankerModelId;
                _rerankerModel = await LoadModelWithProgressAsync(id, ct);
            }
            return _rerankerModel;
        }
        finally
        {
            _rerankerLock.Release();
        }
    }

    public async Task<LM> GetSpeechModelAsync(string? modelId = null, CancellationToken ct = default)
    {
        if (_speechModel != null) return _speechModel;
        await _speechLock.WaitAsync(ct);
        try
        {
            if (_speechModel == null)
            {
                var id = modelId ?? DefaultSpeechModelId;
                _speechModel = await LoadModelWithProgressAsync(id, ct);
            }
            return _speechModel;
        }
        finally
        {
            _speechLock.Release();
        }
    }

    public async Task<LM> GetSegmentationModelAsync(string? modelId = null, CancellationToken ct = default)
    {
        if (_segmentationModel != null) return _segmentationModel;
        await _segmentationLock.WaitAsync(ct);
        try
        {
            if (_segmentationModel == null)
            {
                var id = modelId ?? DefaultSegmentationModelId;
                _segmentationModel = await LoadModelWithProgressAsync(id, ct);
            }
            return _segmentationModel;
        }
        finally
        {
            _segmentationLock.Release();
        }
    }

    public void Dispose()
    {
        // Unblock anyone parked on the chat gate FIRST. SemaphoreSlim.Dispose() does not
        // wake its waiters, so without this a shutdown while a turn is in flight leaves
        // every queued caller hanging on a semaphore that is about to disappear.
        _chatQueue.SignalShutdown();

        // Roles can now ALIAS one another (embedding + reranker share one LM when they
        // resolve to the same file), so dispose reference-distinct instances exactly once.
        var disposed = new HashSet<object>(ReferenceEqualityComparer.Instance);
        foreach (var model in new[]
        {
            _chatModel, _visionModel, _embeddingModel,
            _speechModel, _rerankerModel, _segmentationModel
        }.Concat(_sharedModels.LoadedValues))
        {
            if (model is not null && disposed.Add(model)) model.Dispose();
        }

        _chatLock.Dispose();
        _visionLock.Dispose();
        _embeddingLock.Dispose();
        _speechLock.Dispose();
        _rerankerLock.Dispose();
        _segmentationLock.Dispose();
        _chatInferenceGate.Dispose();
        _visionInferenceGate.Dispose();
        _embeddingInferenceGate.Dispose();
        _speechInferenceGate.Dispose();
        _rerankerInferenceGate.Dispose();
        _segmentationInferenceGate.Dispose();
        _chatQueue.Dispose();
    }

    /// <summary>Test seam: how many distinct models are actually held in memory.</summary>
    internal int LoadedModelCount => _sharedModels.LoadedValues.Count;

    private sealed class SemaphoreLease : IAsyncDisposable
    {
        private SemaphoreSlim? _semaphore;
        public SemaphoreLease(SemaphoreSlim semaphore) => _semaphore = semaphore;

        public ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref _semaphore, null)?.Release();
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>
/// Keyed, load-once cache of expensive singletons. Callers asking for the same key get the
/// SAME instance and, when they arrive concurrently, wait on the SAME in-flight load instead
/// of starting a second one. A failed load is evicted so the next caller may retry.
/// </summary>
internal sealed class SharedInstanceCache<T> where T : class
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Task<T>> _entries = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Instances that finished loading successfully (ownership stays with the cache).</summary>
    public IReadOnlyCollection<T> LoadedValues
    {
        get
        {
            lock (_gate)
            {
                return _entries.Values
                    .Where(entry => entry.IsCompletedSuccessfully)
                    .Select(entry => entry.Result)
                    .ToArray();
            }
        }
    }

    public async Task<T> GetOrLoadAsync(string key, Func<CancellationToken, Task<T>> loader, CancellationToken ct)
    {
        Task<T> load;
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out load!))
            {
                // Started inside the lock but NOT awaited there: the lock only guards the
                // dictionary, so a slow load never blocks a lookup for a different key.
                load = loader(ct);
                _entries[key] = load;
            }
        }

        try
        {
            return await load;
        }
        catch
        {
            lock (_gate)
            {
                if (_entries.TryGetValue(key, out var current)
                    && ReferenceEquals(current, load)
                    && !current.IsCompletedSuccessfully)
                {
                    _entries.Remove(key);
                }
            }

            throw;
        }
    }
}
