using LMKit.Model;

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

    public string DefaultChatModelId { get; set; }
    public string DefaultVisionModelId { get; set; }
    public string DefaultEmbeddingModelId { get; set; }
    public string DefaultSpeechModelId { get; set; }
    public string DefaultRerankerModelId { get; set; }
    public string DefaultSegmentationModelId { get; set; }

    public LmModelManager(Microsoft.Extensions.Configuration.IConfiguration configuration)
    {
        var config = configuration.GetSection("AiModels");
        DefaultChatModelId = config["DefaultChat"] ?? "qwen3.5:2b";
        DefaultVisionModelId = config["DefaultVision"] ?? "paddleocr-vl-1.6:0.9b";
        DefaultEmbeddingModelId = config["DefaultEmbedding"] ?? "gemma3:270m";
        DefaultSpeechModelId = config["DefaultSpeech"] ?? "whisper-tiny";
        DefaultRerankerModelId = config["DefaultReranker"] ?? "bge-reranker-v2-m3";
        DefaultSegmentationModelId = config["DefaultSegmentation"] ?? "u2net";

        var limits = configuration.GetSection("SemaphoreLimits");
        int chatLimit = limits.GetValue<int>("Chat", 1);
        int visionLimit = limits.GetValue<int>("Vision", 1);
        int embeddingLimit = limits.GetValue<int>("Embedding", 1);
        int speechLimit = limits.GetValue<int>("Speech", 1);
        int rerankerLimit = limits.GetValue<int>("Reranker", 1);
        int segmentationLimit = limits.GetValue<int>("Segmentation", 1);

        _chatLock = new SemaphoreSlim(chatLimit, chatLimit);
        _visionLock = new SemaphoreSlim(visionLimit, visionLimit);
        _embeddingLock = new SemaphoreSlim(embeddingLimit, embeddingLimit);
        _speechLock = new SemaphoreSlim(speechLimit, speechLimit);
        _rerankerLock = new SemaphoreSlim(rerankerLimit, rerankerLimit);
        _segmentationLock = new SemaphoreSlim(segmentationLimit, segmentationLimit);
    }
    private async Task<LM> LoadModelWithProgressAsync(string id)
    {
        if (id.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || id.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            // Tự động chuyển link /blob/ sang /resolve/ của HuggingFace để lấy file RAW
            if (id.Contains("huggingface.co") && id.Contains("/blob/"))
            {
                id = id.Replace("/blob/", "/resolve/");
            }

            var fileName = Path.GetFileName(new Uri(id).LocalPath);
            if (string.IsNullOrEmpty(fileName)) fileName = "model.gguf";
            
            var modelsDir = Path.Combine(Directory.GetCurrentDirectory(), "Models");
            Directory.CreateDirectory(modelsDir);
            var localPath = Path.Combine(modelsDir, fileName);

            if (!File.Exists(localPath))
            {
                Console.WriteLine($"[LmModelManager] Đang bắt đầu tải model từ: {id}");
                using var client = new HttpClient();
                using var response = await client.GetAsync(id, HttpCompletionOption.ResponseHeadersRead);
                response.EnsureSuccessStatusCode();

                var totalBytes = response.Content.Headers.ContentLength ?? -1L;
                var canReportProgress = totalBytes != -1 && totalBytes != 0;

                using var contentStream = await response.Content.ReadAsStreamAsync();
                using var fileStream = new FileStream(localPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);

                var buffer = new byte[8192];
                var totalRead = 0L;
                var bytesRead = 0;
                int lastProgress = -1;

                while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length)) != 0)
                {
                    await fileStream.WriteAsync(buffer, 0, bytesRead);
                    totalRead += bytesRead;

                    if (canReportProgress)
                    {
                        var progress = (int)((totalRead * 100) / totalBytes);
                        if (progress != lastProgress && progress % 5 == 0) // Report every 5%
                        {
                            Console.WriteLine($"[LmModelManager] Tiến trình tải model: {progress}% ({totalRead / (1024 * 1024)}MB / {totalBytes / (1024 * 1024)}MB)");
                            lastProgress = progress;
                        }
                    }
                }
                Console.WriteLine($"[LmModelManager] Tải model hoàn tất, lưu tại: {localPath}");
            }
            else
            {
                Console.WriteLine($"[LmModelManager] Model đã tồn tại tại {localPath}, bỏ qua bước tải.");
            }
            
            id = localPath; // Gán lại ID bằng đường dẫn local
        }

        Console.WriteLine($"[LmModelManager] Khởi tạo model {id} (Có thể mất vài giây nạp vào RAM)...");
        var model = await Task.Run(() => LM.LoadFromModelID(id));
        Console.WriteLine($"[LmModelManager] Đã tải model thành công.");
        return model;
    }

    public async Task<LM> GetChatModelAsync(string? modelId = null)
    {
        if (_chatModel != null) return _chatModel;
        await _chatLock.WaitAsync();
        try
        {
            if (_chatModel == null)
            {
                var id = modelId ?? DefaultChatModelId;
                _chatModel = await LoadModelWithProgressAsync(id);
            }
            return _chatModel;
        }
        finally
        {
            _chatLock.Release();
        }
    }

    public async Task<LM> GetVisionModelAsync(string? modelId = null)
    {
        if (_visionModel != null) return _visionModel;
        await _visionLock.WaitAsync();
        try
        {
            if (_visionModel == null)
            {
                var id = modelId ?? DefaultVisionModelId;
                _visionModel = await LoadModelWithProgressAsync(id);
            }
            return _visionModel;
        }
        finally
        {
            _visionLock.Release();
        }
    }

    public async Task<LM> GetEmbeddingModelAsync(string? modelId = null)
    {
        if (_embeddingModel != null) return _embeddingModel;
        await _embeddingLock.WaitAsync();
        try
        {
            if (_embeddingModel == null)
            {
                var id = modelId ?? DefaultEmbeddingModelId;
                _embeddingModel = await LoadModelWithProgressAsync(id);
            }
            return _embeddingModel;
        }
        finally
        {
            _embeddingLock.Release();
        }
    }

    public async Task<LM> GetRerankerModelAsync(string? modelId = null)
    {
        if (_rerankerModel != null) return _rerankerModel;
        await _rerankerLock.WaitAsync();
        try
        {
            if (_rerankerModel == null)
            {
                var id = modelId ?? DefaultRerankerModelId;
                _rerankerModel = await LoadModelWithProgressAsync(id);
            }
            return _rerankerModel;
        }
        finally
        {
            _rerankerLock.Release();
        }
    }

    public async Task<LM> GetSpeechModelAsync(string? modelId = null)
    {
        if (_speechModel != null) return _speechModel;
        await _speechLock.WaitAsync();
        try
        {
            if (_speechModel == null)
            {
                var id = modelId ?? DefaultSpeechModelId;
                _speechModel = await LoadModelWithProgressAsync(id);
            }
            return _speechModel;
        }
        finally
        {
            _speechLock.Release();
        }
    }

    public async Task<LM> GetSegmentationModelAsync(string? modelId = null)
    {
        if (_segmentationModel != null) return _segmentationModel;
        await _segmentationLock.WaitAsync();
        try
        {
            if (_segmentationModel == null)
            {
                var id = modelId ?? DefaultSegmentationModelId;
                _segmentationModel = await LoadModelWithProgressAsync(id);
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
    }
}
