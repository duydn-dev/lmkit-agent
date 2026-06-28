using LMKit.Model;

namespace LmKitOmniApi.Services;

public class LmModelManager : IDisposable
{
    private LM? _chatModel;
    private LM? _visionModel;
    private LM? _embeddingModel;
    private LM? _speechModel;
    private LM? _rerankerModel;

    // M2 Fix: Per-model locks to prevent cross-model blocking.
    // Previously a single SemaphoreSlim(1,1) blocked ALL model loads — if chat model
    // took 30s to load, embedding/vision/reranker requests were all queued behind it.
    private readonly SemaphoreSlim _chatLock = new(1, 1);
    private readonly SemaphoreSlim _visionLock = new(1, 1);
    private readonly SemaphoreSlim _embeddingLock = new(1, 1);
    private readonly SemaphoreSlim _speechLock = new(1, 1);
    private readonly SemaphoreSlim _rerankerLock = new(1, 1);

    public string DefaultChatModelId { get; set; }
    public string DefaultVisionModelId { get; set; }
    public string DefaultEmbeddingModelId { get; set; }
    public string DefaultSpeechModelId { get; set; }
    public string DefaultRerankerModelId { get; set; }

    public LmModelManager(Microsoft.Extensions.Configuration.IConfiguration configuration)
    {
        var config = configuration.GetSection("AiModels");
        DefaultChatModelId = config["DefaultChat"] ?? "qwen3.5:2b";
        DefaultVisionModelId = config["DefaultVision"] ?? "paddleocr-vl-1.6:0.9b";
        DefaultEmbeddingModelId = config["DefaultEmbedding"] ?? "gemma3:270m";
        DefaultSpeechModelId = config["DefaultSpeech"] ?? "whisper-tiny";
        DefaultRerankerModelId = config["DefaultReranker"] ?? "bge-reranker-v2-m3";
    }
    private LM LoadModelWithProgress(string id)
    {
        Console.WriteLine($"[LmModelManager] Initializing model {id}...");
        var model = LM.LoadFromModelID(id);
        Console.WriteLine($"[LmModelManager] Successfully loaded {id}.");
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
                _chatModel = LoadModelWithProgress(id);
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
                _visionModel = LoadModelWithProgress(id);
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
                _embeddingModel = LoadModelWithProgress(id);
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
                _rerankerModel = LoadModelWithProgress(id);
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
                _speechModel = LoadModelWithProgress(id);
            }
            return _speechModel;
        }
        finally
        {
            _speechLock.Release();
        }
    }

    public void Dispose()
    {
        _chatModel?.Dispose();
        _visionModel?.Dispose();
        _embeddingModel?.Dispose();
        _speechModel?.Dispose();
        _rerankerModel?.Dispose(); // L2 Fix: was missing, causing resource leak
        _chatLock.Dispose();
        _visionLock.Dispose();
        _embeddingLock.Dispose();
        _speechLock.Dispose();
        _rerankerLock.Dispose();
    }
}
