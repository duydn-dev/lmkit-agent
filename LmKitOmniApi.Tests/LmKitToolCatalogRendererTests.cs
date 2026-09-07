using LMKit.Agents.Tools;
using LmKitOmniApi.Infrastructure.AI.Tools;
using Xunit;

namespace LmKitOmniApi.Tests;

/// <summary>
/// Guards the reflective bind in <see cref="LmKitToolCatalogRenderer"/>.
///
/// <para>
/// The renderer reaches an internal, name-obfuscated LM-Kit type by SHAPE, because LM-Kit renders
/// the tool catalog only when <c>ChatHistory.MessageCount == 0</c> and exposes no public way to
/// produce that block for a rebuilt history. The bind failing is not a crash — the factory
/// degrades to seeding the persona alone, i.e. back to the defect where function calling stops
/// after the first message. These tests exist so that degradation is LOUD: an LM-Kit upgrade that
/// reshapes the internal fails here rather than quietly switching tool calling off in production.
/// </para>
/// <para>
/// No model weights required: binding is pure reflection over the LM-Kit assembly. The live proof
/// that the bound method produces the block LM-Kit itself would render is in
/// <c>ChatConversationFactoryLiveTests</c>.
/// </para>
/// </summary>
public class LmKitToolCatalogRendererTests
{
    /// <summary>
    /// THE canary. If this fails after an LM-Kit upgrade, the tool catalog is no longer reaching
    /// the model on turns 2..n — read <see cref="LmKitToolCatalogRenderer.Diagnostics"/> and
    /// re-derive the shape from the new assembly before shipping.
    /// </summary>
    [Fact]
    public void Renderer_BindsToThisLmKitVersion()
    {
        Assert.True(
            LmKitToolCatalogRenderer.IsAvailable,
            "LM-Kit's tool-catalog renderer did not bind, so the tool catalog will be missing from "
            + "every turn after the first. Diagnostics: " + LmKitToolCatalogRenderer.Diagnostics);
    }

    [Fact]
    public void Diagnostics_NamesTheBoundType()
    {
        Assert.Contains("bound to", LmKitToolCatalogRenderer.Diagnostics, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_NullModel_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => LmKitToolCatalogRenderer.Render(null!, "prompt", [new FakeTool()]));
    }

    private sealed class FakeTool : ITool
    {
        public string Name => "fake_tool";
        public string Description => "does nothing";
        public string InputSchema => """{"type":"object","properties":{}}""";
        public Task<string> InvokeAsync(string arguments, CancellationToken cancellationToken = default)
            => Task.FromResult("{}");
    }
}
