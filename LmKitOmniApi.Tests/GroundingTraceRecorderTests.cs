using LmKitOmniApi.Infrastructure.AI.ComputerUse.Training;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LmKitOmniApi.Tests;

/// <summary>
/// CI tests for <see cref="FileGroundingTraceRecorder"/> — the model-free training-data
/// capture half of the grounding pipeline. Proven with a real temp directory (no model, no
/// database): the record→read roundtrip, the disabled no-op, and tenant isolation.
/// </summary>
public sealed class GroundingTraceRecorderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"lmkit-grounding-{Guid.NewGuid():N}");
    private readonly Guid _tenantId = Guid.NewGuid();

    private FileGroundingTraceRecorder CreateRecorder(bool enabled = true)
    {
        var options = new GroundingTrainingOptions { Enabled = enabled, DatasetPath = _root };
        return new FileGroundingTraceRecorder(Options.Create(options), NullLogger<FileGroundingTraceRecorder>.Instance);
    }

    private GroundingSample Sample(Guid? tenant = null, string goal = "book a table") => new()
    {
        TenantId = tenant ?? _tenantId,
        TaskGoal = goal,
        PageUrl = "https://example.com/reserve",
        ElementsText = "INTERACTIVE ELEMENTS:\n  [3] button: Reserve\n",
        ScreenshotFileId = "shot-1.png",
        SystemPrompt = "You are a careful web automation agent.",
        CorrectActionJson = "{\"action\":\"click\",\"ref\":3}",
        Source = "approved",
    };

    // ── record → read roundtrip ──

    [Fact]
    public async Task RecordAsync_ThenReadAsync_RoundtripsAllFields()
    {
        var recorder = CreateRecorder();
        var original = Sample();

        await recorder.RecordAsync(original, CancellationToken.None);

        var read = await recorder.ReadAsync(_tenantId);
        var one = Assert.Single(read);
        Assert.Equal(original.Id, one.Id);
        Assert.Equal(original.TenantId, one.TenantId);
        Assert.Equal(original.TaskGoal, one.TaskGoal);
        Assert.Equal(original.PageUrl, one.PageUrl);
        Assert.Equal(original.ElementsText, one.ElementsText);
        Assert.Equal(original.ScreenshotFileId, one.ScreenshotFileId);
        Assert.Equal(original.SystemPrompt, one.SystemPrompt);
        Assert.Equal(original.CorrectActionJson, one.CorrectActionJson);
        Assert.Equal(original.Source, one.Source);
        Assert.Equal(1, await recorder.CountAsync(_tenantId));
    }

    [Fact]
    public async Task RecordAsync_AppendsManySamples_InOrder()
    {
        var recorder = CreateRecorder();
        await recorder.RecordAsync(Sample(goal: "first"), CancellationToken.None);
        await recorder.RecordAsync(Sample(goal: "second"), CancellationToken.None);
        await recorder.RecordAsync(Sample(goal: "third"), CancellationToken.None);

        var read = await recorder.ReadAsync(_tenantId);
        Assert.Equal(3, read.Count);
        Assert.Equal(new[] { "first", "second", "third" }, read.Select(s => s.TaskGoal));
        Assert.Equal(3, await recorder.CountAsync(_tenantId));
    }

    // ── disabled → no-op ──

    [Fact]
    public async Task Disabled_RecordAsync_IsNoOp_AndReadsAreEmpty()
    {
        var recorder = CreateRecorder(enabled: false);
        Assert.False(recorder.Enabled);

        await recorder.RecordAsync(Sample(), CancellationToken.None);

        Assert.Empty(await recorder.ReadAsync(_tenantId));
        Assert.Equal(0, await recorder.CountAsync(_tenantId));
        // Nothing at all should have been written to disk.
        Assert.False(Directory.Exists(Path.Combine(_root, _tenantId.ToString("N"))));
    }

    // ── tenant isolation ──

    [Fact]
    public async Task ReadAsync_IsTenantScoped()
    {
        var recorder = CreateRecorder();
        var otherTenant = Guid.NewGuid();

        await recorder.RecordAsync(Sample(), CancellationToken.None);
        await recorder.RecordAsync(Sample(tenant: otherTenant, goal: "other-tenant"), CancellationToken.None);

        Assert.Single(await recorder.ReadAsync(_tenantId));
        var other = Assert.Single(await recorder.ReadAsync(otherTenant));
        Assert.Equal("other-tenant", other.TaskGoal);
        Assert.Empty(await recorder.ReadAsync(Guid.NewGuid()));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { /* best effort */ }
    }
}
