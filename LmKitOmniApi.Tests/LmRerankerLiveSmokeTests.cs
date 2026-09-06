using LmKitOmniApi.Services;
using Microsoft.Extensions.Configuration;
using Xunit;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace LmKitOmniApi.Tests;

/// <summary>
/// LIVE smoke test: proves the local Bge-M3 GGUF under LmKitOmniApi/AIModels
/// loads through the AiModels:Models registry as a RERANKER (the default
/// reranker slot points at the same "bge-m3" registry key as the embedding
/// slot, so a broken/missing file must surface here, not in production RAG).
/// Skips (never fails) when the model file is absent.
/// </summary>
public class LmRerankerLiveSmokeTests
{
    private readonly ITestOutputHelper _output;

    public LmRerankerLiveSmokeTests(ITestOutputHelper output) => _output = output;

    private static string? FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; depth < 10 && directory is not null; depth++)
        {
            if (File.Exists(Path.Combine(directory.FullName, "LmKitOmniApi", "LmKitOmniApi.csproj")))
                return directory.FullName;
            directory = directory.Parent;
        }

        return null;
    }

    private static (LmModelManager Manager, string AiModelsDir)? LoadBgeM3ViaRegistry()
    {
        var root = FindRepositoryRoot();
        var model = root is null
            ? null
            : Path.Combine(root, "LmKitOmniApi", "AIModels", "Bge-M3-568M-Q4_K_M.gguf");
        Skip.IfNot(model is not null && File.Exists(model), "Bge-M3 GGUF not present; skipping the live reranker smoke test.");

        var aiModelsDir = Path.Combine(root!, "LmKitOmniApi", "AIModels");
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AiModels:ModelsDirectory"] = aiModelsDir,
                ["AiModels:DefaultReranker"] = "bge-m3",
                ["AiModels:Models:bge-m3:Path"] = "Bge-M3-568M-Q4_K_M.gguf"
            })
            .Build();

        return (new LmModelManager(configuration), aiModelsDir);
    }

    [SkippableFact]
    public async Task Registry_LoadsBgeM3GgufAsReranker()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var loaded = LoadBgeM3ViaRegistry();
        using var manager = loaded!.Value.Manager;
        var aiModelsDir = loaded.Value.AiModelsDir;

        var model = await manager.GetRerankerModelAsync(ct: timeout.Token);

        Assert.NotNull(model);
        _output.WriteLine($"Reranker loaded from {aiModelsDir} (Bge-M3-568M-Q4_K_M.gguf).");
    }
}
