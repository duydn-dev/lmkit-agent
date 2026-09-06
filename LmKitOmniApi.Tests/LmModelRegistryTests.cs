using LmKitOmniApi.Services;
using Microsoft.Extensions.Configuration;

namespace LmKitOmniApi.Tests;

/// <summary>
/// Tests for the AiModels:Models local model registry — path resolution and load
/// branch selection. No native LM-Kit model is loaded; registry resolution is pure.
/// </summary>
public class LmModelRegistryTests : IDisposable
{
    private readonly string _modelsDirectory;
    private readonly string _previousDirectory;

    public LmModelRegistryTests()
    {
        _previousDirectory = Directory.GetCurrentDirectory();
        _modelsDirectory = Path.Combine(Path.GetTempPath(), $"lmkit-registry-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(_modelsDirectory, "testmodel"));
        Directory.SetCurrentDirectory(_modelsDirectory);
    }

    public void Dispose()
    {
        Directory.SetCurrentDirectory(_previousDirectory);
        try { Directory.Delete(_modelsDirectory, recursive: true); }
        catch (IOException) { /* best effort cleanup */ }
    }

    private static IConfigurationSection ParseModelsSection(params (string Key, string Value)[] pairs)
    {
        var data = pairs.ToDictionary(p => $"AiModels:Models:{p.Key}", p => (string?)p.Value);
        var builder = new ConfigurationBuilder().AddInMemoryCollection(data);
        return builder.Build().GetSection("AiModels:Models");
    }

    [Fact]
    public void ParseRegisteredModels_ResolvesRelativePathAgainstModelsDirectory()
    {
        var section = ParseModelsSection(("bonsai:Path", "bonsai/Bonsai-27B-Q1_0.gguf"));

        var models = LmModelManager.ParseRegisteredModelsForTests(_modelsDirectory, section);

        var registered = Assert.Single(models.Values);
        Assert.Equal(
            Path.Combine(_modelsDirectory, "bonsai", "Bonsai-27B-Q1_0.gguf"),
            registered.ResolvedModelPath);
        Assert.Null(registered.ResolvedMmprojPath);
    }

    [Fact]
    public void ParseRegisteredModels_WithMmprojMarksTwoFileVisionModel()
    {
        var section = ParseModelsSection(
            ("bonsai:Path", "bonsai/model.gguf"),
            ("bonsai:Mmproj", "bonsai/mmproj.gguf"));

        var models = LmModelManager.ParseRegisteredModelsForTests(_modelsDirectory, section);

        var registered = Assert.Single(models.Values);
        Assert.Equal(Path.Combine(_modelsDirectory, "bonsai", "mmproj.gguf"), registered.ResolvedMmprojPath);
    }

    [Fact]
    public void ParseRegisteredModels_AcceptsAbsolutePaths()
    {
        var absolute = Path.Combine(_modelsDirectory, "elsewhere", "model.gguf");
        var section = ParseModelsSection(("abs:Path", absolute));

        var models = LmModelManager.ParseRegisteredModelsForTests(_modelsDirectory, section);

        Assert.Equal(absolute, Assert.Single(models.Values).ResolvedModelPath);
    }

    [Theory]
    [InlineData("Path", "../escape.gguf")]
    [InlineData("Path", "sub/../../escape.gguf")]
    [InlineData("Mmproj", "../escape-mmproj.gguf")]
    public void ParseRegisteredModels_RejectsParentTraversalOutsideModelsDirectory(string setting, string value)
    {
        var section = ParseModelsSection(($"bad:{setting}", value));

        var error = Assert.Throws<InvalidOperationException>(
            () => LmModelManager.ParseRegisteredModelsForTests(_modelsDirectory, section));

        Assert.Contains("AiModels:Models:bad", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseRegisteredModels_MissingPathThrows()
    {
        var section = ParseModelsSection(("broken:Mmproj", "bonsai/mmproj.gguf"));

        var error = Assert.Throws<InvalidOperationException>(
            () => LmModelManager.ParseRegisteredModelsForTests(_modelsDirectory, section));

        Assert.Contains("AiModels:Models:broken:Path", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Constructor_WithRegisteredModelConfiguration_DoesNotThrowAndKeepsDefaults()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AiModels:ModelsDirectory"] = "AIModels",
                ["AiModels:DefaultChat"] = "bonsai",
                ["AiModels:Models:bonsai:Path"] = "bonsai/model.gguf",
                ["SemaphoreLimits:Chat"] = "1"
            })
            .Build();

        using var manager = new LmModelManager(configuration);

        Assert.Equal("bonsai", manager.DefaultChatModelId);
        var registered = manager.ResolveRegisteredModelForTests("bonsai");
        Assert.NotNull(registered);
        Assert.EndsWith(Path.Combine("AIModels", "bonsai", "model.gguf"), registered!.ResolvedModelPath, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveRegisteredModel_UnknownOrEmptyIdReturnsNull()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AiModels:Models:bonsai:Path"] = "bonsai/model.gguf"
            })
            .Build();
        using var manager = new LmModelManager(configuration);

        Assert.Null(manager.ResolveRegisteredModelForTests("qwen3.5:2b"));
        Assert.Null(manager.ResolveRegisteredModelForTests("https://huggingface.co/org/model.gguf"));
        Assert.Null(manager.ResolveRegisteredModelForTests(null));
    }

    [Fact]
    public void ResolveModelsDirectory_DefaultsToAiModelsAndHonorsAbsoluteOverride()
    {
        Assert.EndsWith(
            Path.Combine("AIModels", string.Empty).TrimEnd(Path.DirectorySeparatorChar),
            LmModelManager.ResolveModelsDirectory(null),
            StringComparison.Ordinal);

        var absolute = Path.Combine(Path.GetTempPath(), "custom-models");
        Assert.Equal(absolute, LmModelManager.ResolveModelsDirectory(absolute));
    }
}
