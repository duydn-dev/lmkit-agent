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
    private readonly long _maxDownloadBytes;
    private readonly TimeSpan _downloadTimeout;
    private readonly ILogger<LmModelManager> _logger;

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
        DefaultChatModelId = config["DefaultChat"] ?? "qwen3.5:2b";
        DefaultVisionModelId = config["DefaultVision"] ?? "paddleocr-vl-1.6:0.9b";
        DefaultEmbeddingModelId = config["DefaultEmbedding"] ?? "gemma3:270m";
        DefaultSpeechModelId = config["DefaultSpeech"] ?? "whisper-tiny";
        DefaultRerankerModelId = config["DefaultReranker"] ?? "bge-reranker-v2-m3";
        DefaultSegmentationModelId = config["DefaultSegmentation"] ?? "u2net";
        _maxDownloadBytes = config.GetValue<long>("MaxDownloadBytes", 8L * 1024 * 1024 * 1024);
        if (_maxDownloadBytes <= 0)
            throw new InvalidOperationException("AiModels:MaxDownloadBytes must be greater than zero.");
        var timeoutMinutes = config.GetValue<int>("DownloadTimeoutMinutes", 30);
        if (timeoutMinutes is < 1 or > 180)
            throw new InvalidOperationException("AiModels:DownloadTimeoutMinutes must be between 1 and 180.");
        _downloadTimeout = TimeSpan.FromMinutes(timeoutMinutes);

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
    }

    private static int GetPositiveLimit(IConfigurationSection section, string name, int fallback)
    {
        var value = section.GetValue<int>(name, fallback);
        if (value <= 0)
            throw new InvalidOperationException($"SemaphoreLimits:{name} must be greater than zero.");
        return value;
    }
    private async Task<LM> LoadModelWithProgressAsync(string id, CancellationToken ct = default)
    {
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
            
            var modelsDir = Path.Combine(Directory.GetCurrentDirectory(), "Models");
            Directory.CreateDirectory(modelsDir);
            var localPath = Path.Combine(modelsDir, fileName);

            if (!File.Exists(localPath))
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
                var temporaryPath = localPath + $".{Guid.NewGuid():N}.download";

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

                    File.Move(temporaryPath, localPath);
                }
                finally
                {
                    if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
                }
                _logger.LogInformation("Configured model download completed at {LocalPath}", localPath);
            }
            else
            {
                _logger.LogInformation("Using existing configured model at {LocalPath}", localPath);
            }
            
            id = localPath; // Gán lại ID bằng đường dẫn local
        }

        _logger.LogInformation("Loading model {ModelId}", id);
        var model = await Task.Run(() => LM.LoadFromModelID(id), ct);
        _logger.LogInformation("Model loaded successfully");
        return model;
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

    internal static bool IsPrivateOrLocalAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address)) return true;
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            return bytes[0] == 10
                || bytes[0] == 127
                || bytes[0] == 0
                || (bytes[0] == 169 && bytes[1] == 254)
                || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
                || (bytes[0] == 192 && bytes[1] == 168);
        }

        return address.IsIPv6LinkLocal
            || address.IsIPv6SiteLocal
            || address.Equals(IPAddress.IPv6Loopback)
            || address.Equals(IPAddress.IPv6Any);
    }

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
                    LastChatModelLoadError = ex.GetType().Name;
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

    public async ValueTask<IAsyncDisposable> AcquireChatInferenceAsync(CancellationToken ct = default)
        => await AcquireInferenceAsync(_chatInferenceGate, ct);

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
        _chatModel?.Dispose();
        _visionModel?.Dispose();
        _embeddingModel?.Dispose();
        _speechModel?.Dispose();
        _rerankerModel?.Dispose();
        _segmentationModel?.Dispose(); // L2 Fix: was missing, causing resource leak
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
    }

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
