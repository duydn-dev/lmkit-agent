using LmKitOmniApi.Services;
using Microsoft.Extensions.Configuration;

namespace LmKitOmniApi.Tests;

/// <summary>
/// Tests for the AiModels:Models local model registry — path resolution and load
/// branch selection. No native LM-Kit model is loaded; registry resolution is pure.
///
/// PROCESS-GLOBAL STATE — DO NOT REINTRODUCE: this fixture used to call
/// <c>Directory.SetCurrentDirectory</c> so the relative-path cases would resolve under a
/// temp folder. The current directory is per-PROCESS, xUnit runs collections in parallel,
/// and every other test in flight saw that temp folder as its working directory for the
/// lifetime of this class. It made
/// <see cref="ToolSecurityPolicyTests.FileSandbox_DoesNotAcceptSiblingWithAllowedPrefix"/>
/// fail intermittently: that test builds "&lt;cwd&gt;/Uploads/payload.txt" at assert time and
/// expects <c>ToolSandboxService</c> to allow it, but the sandbox roots were anchored to the
/// real working directory. Nothing here needs a particular working directory — the parse
/// cases pass <see cref="_modelsDirectory"/> explicitly, and the three cases that build a
/// real <c>LmModelManager</c> assert with <c>EndsWith</c> on a relative tail, which holds for
/// any cwd. Keep it that way.
/// </summary>
public class LmModelRegistryTests : IDisposable
{
    private readonly string _modelsDirectory;

    public LmModelRegistryTests()
    {
        _modelsDirectory = Path.Combine(Path.GetTempPath(), $"lmkit-registry-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_modelsDirectory);
    }

    public void Dispose()
    {
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

    /// <summary>
    /// Registry resolution is pure string work: a relative <c>AiModels:ModelsDirectory</c> is
    /// turned into an absolute path but NEVER created. This test exists because the fixture no
    /// longer redirects the process working directory — the relative default now resolves under
    /// the test output folder, so a stray <c>Directory.CreateDirectory</c> in the manager's
    /// constructor would start littering <c>bin/</c> with empty <c>AIModels</c>/<c>Models</c>
    /// trees on every run. Fail loudly if that ever changes.
    /// </summary>
    [Fact]
    public void Constructor_WithRelativeModelsDirectory_CreatesNothingOnDisk()
    {
        // A GUID name so the "was it created?" check cannot be confused by anything that
        // already lives in the test output directory.
        var relativeName = $"lmkit-registry-probe-{Guid.NewGuid():N}";
        var expected = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), relativeName));
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AiModels:ModelsDirectory"] = relativeName,
                ["AiModels:DefaultChat"] = "bonsai",
                ["AiModels:Models:bonsai:Path"] = "bonsai/model.gguf"
            })
            .Build();

        using var manager = new LmModelManager(configuration);

        Assert.Equal(
            Path.Combine(expected, "bonsai", "model.gguf"),
            manager.ResolveRegisteredModelForTests("bonsai")!.ResolvedModelPath);
        Assert.False(
            Directory.Exists(expected),
            $"LmModelManager created '{expected}'. Registry resolution must not touch the disk, " +
            "or every test run will litter the output directory with empty model folders.");
    }
}
