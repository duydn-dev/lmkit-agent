using System.Diagnostics;
using LMKit.TextGeneration;
using LMKit.TextGeneration.Chat;
using LmKitOmniApi.Services;
using Microsoft.Extensions.Configuration;
using Xunit;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace LmKitOmniApi.Tests;

/// <summary>
/// LIVE smoke tests for the AiModels:Models registry against the real Bonsai-27B
/// GGUF + mmproj projector pair under LmKitOmniApi/AIModels/bonsai. They prove the
/// two-file layout actually loads and that the projector yields working vision.
/// Each test skips (never fails) when the model files or image fixture are absent,
/// so machines without the multi-GB weights stay green.
/// </summary>
public class LmModelLiveSmokeTests
{
    private readonly ITestOutputHelper _output;

    public LmModelLiveSmokeTests(ITestOutputHelper output) => _output = output;

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

    private static (string AiModelsDir, string ImagePath)? LocateLiveFixture()
    {
        var root = FindRepositoryRoot();
        if (root is null) return null;

        var aiModels = Path.Combine(root, "LmKitOmniApi", "AIModels");
        var gguf = Path.Combine(aiModels, "bonsai", "Bonsai-27B-Q1_0.gguf");
        var mmproj = Path.Combine(aiModels, "bonsai", "Bonsai-27B-mmproj-BF16.gguf");
        var image = Path.Combine(root, "console_net", "vision", "image-embeddings", "image_similarity_search", "dog1.jpg");
        if (!File.Exists(gguf) || !File.Exists(mmproj) || !File.Exists(image)) return null;
        return (aiModels, image);
    }

    private static (LmModelManager Manager, string ImagePath) LoadBonsaiViaRegistry()
    {
        var fixture = LocateLiveFixture();
        Skip.IfNot(
            fixture.HasValue,
            "Bonsai GGUF/mmproj or image fixture not present; skipping the live model smoke test.");

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AiModels:ModelsDirectory"] = fixture!.Value.AiModelsDir,
                ["AiModels:DefaultChat"] = "bonsai",
                ["AiModels:Models:bonsai:Path"] = "bonsai/Bonsai-27B-Q1_0.gguf",
                ["AiModels:Models:bonsai:Mmproj"] = "bonsai/Bonsai-27B-mmproj-BF16.gguf"
            })
            .Build();

        return (new LmModelManager(configuration), fixture.Value.ImagePath);
    }

    [SkippableFact]
    public async Task Registry_LoadsBonsaiGgufWithMmprojAndReportsVisionCapability()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var loaded = LoadBonsaiViaRegistry();
        using var manager = loaded.Manager;

        var model = await manager.GetChatModelAsync(ct: timeout.Token);

        Assert.NotNull(model);
        Assert.True(
            model.HasVision,
            "Model loaded but vision capability is missing — the mmproj projector was likely not applied.");
    }

    [SkippableFact]
    public async Task Registry_BonsaiAnswersGroundedVisionQuestionAboutDogPhoto()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(12));
        var loaded = LoadBonsaiViaRegistry();
        using var manager = loaded.Manager;

        var model = await manager.GetChatModelAsync(ct: timeout.Token);
        await using var lease = await manager.AcquireVisionInferenceAsync(timeout.Token);

        var conversation = new MultiTurnConversation(model);
        var attachment = new LMKit.Data.Attachment(loaded.ImagePath);
        var message = new ChatHistory.Message(
            "Look at the attached photo. Which animal is in it? Answer with one word.",
            attachment);
        var result = conversation.Submit(message, timeout.Token);

        Assert.False(
            string.IsNullOrWhiteSpace(result.Completion),
            "Model returned an empty completion for the vision question.");
        var answer = result.Completion.ToLowerInvariant();
        var recognizedDog = answer.Contains("dog") || answer.Contains("chó") || answer.Contains("犬");
        Assert.True(
            recognizedDog,
            $"Expected the model to recognize the dog photo. Actual answer: {result.Completion}");
    }

    /// <summary>
    /// Measures vision inference latency through the exact production path
    /// (registry → LmModelManager → MultiTurnConversation). Prints a breakdown:
    /// cold model load, first image encode+answer, second image, and a repeat
    /// image. Assertions are deliberately light — this is a timing report.
    /// </summary>
    [SkippableFact]
    public async Task Registry_BonsaiVisionLatencyBenchmark()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(12));
        var loaded = LoadBonsaiViaRegistry();
        using var manager = loaded.Manager;

        var loadStart = Stopwatch.GetTimestamp();
        var model = await manager.GetChatModelAsync(ct: timeout.Token);
        _output.WriteLine($"[1] Cold model load (incl. CUDA init): {Stopwatch.GetElapsedTime(loadStart).TotalSeconds:F1}s");

        var root = FindRepositoryRoot();
        Skip.IfNot(
            root is not null
            && File.Exists(Path.Combine(root!, "console_net", "vision", "image-embeddings", "image_similarity_search", "cat1.jpg")),
            "Image fixtures not present.");

        var imageDir = Path.Combine(root!, "console_net", "vision", "image-embeddings", "image_similarity_search");
        var images = new[]
        {
            Path.Combine(imageDir, "dog1.jpg"),
            Path.Combine(imageDir, "cat1.jpg"),
            Path.Combine(imageDir, "dog1.jpg") // repeat: warm projector path
        };
        const string prompt = "Which animal is in the attached photo? Answer with one word.";

        await using var lease = await manager.AcquireVisionInferenceAsync(timeout.Token);
        for (var i = 0; i < images.Length; i++)
        {
            var start = Stopwatch.GetTimestamp();
            var conversation = new MultiTurnConversation(model);
            var attachment = new LMKit.Data.Attachment(images[i]);
            var result = conversation.Submit(new ChatHistory.Message(prompt, attachment), timeout.Token);
            var elapsed = Stopwatch.GetElapsedTime(start);

            Assert.False(string.IsNullOrWhiteSpace(result.Completion));
            _output.WriteLine(
                $"[{i + 2}] {Path.GetFileName(images[i])}: {elapsed.TotalSeconds:F2}s — answer: {result.Completion.Trim()}");
        }
    }
}
