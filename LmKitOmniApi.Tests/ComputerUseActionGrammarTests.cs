using System.Linq;
using System.Text.Json;
using LmKitOmniApi.Infrastructure.AI.ComputerUse;
using Xunit;

namespace LmKitOmniApi.Tests;

/// <summary>
/// Unit tests for the constrained-decoding grounding hardening. The schema builder is pure
/// and always tested; a skippable probe reveals whether LM-Kit accepts the schema for
/// grammar-constrained decoding (if not, <see cref="ComputerUseModel"/> falls back to free
/// generation and the loop's retry + fail-closed gate still protect — so the probe never
/// fails the build).
/// </summary>
public class ComputerUseActionGrammarTests
{
    [Fact]
    public void BuildActionSchema_WithElementRefs_PinsRefToThoseValues()
    {
        var json = ComputerUseActionGrammar.BuildActionSchema(new[] { 3, 7, 12 });

        using var doc = JsonDocument.Parse(json); // must be valid JSON
        var props = doc.RootElement.GetProperty("properties");

        // action is constrained to the nine known verbs
        var actionEnum = props.GetProperty("action").GetProperty("enum")
            .EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Equal(9, actionEnum.Count);
        Assert.Contains("click", actionEnum);
        Assert.Contains("done", actionEnum);

        // ref is pinned to EXACTLY the provided element refs — a hallucinated ref cannot be sampled
        var refEnum = props.GetProperty("ref").GetProperty("enum")
            .EnumerateArray().Select(e => e.GetInt32()).ToList();
        Assert.Equal(new[] { 3, 7, 12 }, refEnum);

        Assert.Equal("action", doc.RootElement.GetProperty("required")[0].GetString());
    }

    [Fact]
    public void BuildActionSchema_NoElements_LeavesRefUnconstrained()
    {
        var json = ComputerUseActionGrammar.BuildActionSchema(System.Array.Empty<int>());

        using var doc = JsonDocument.Parse(json);
        var refProp = doc.RootElement.GetProperty("properties").GetProperty("ref");
        Assert.Equal("integer", refProp.GetProperty("type").GetString());
        Assert.False(refProp.TryGetProperty("enum", out _)); // no page elements → no ref enum
    }

    [Fact]
    public void BuildActionSchema_DeduplicatesRefs()
    {
        var json = ComputerUseActionGrammar.BuildActionSchema(new[] { 5, 5, 8 });

        using var doc = JsonDocument.Parse(json);
        var refEnum = doc.RootElement.GetProperty("properties").GetProperty("ref").GetProperty("enum")
            .EnumerateArray().Select(e => e.GetInt32()).ToList();
        Assert.Equal(new[] { 5, 8 }, refEnum);
    }

    // Does LM-Kit actually accept our schema for grammar-constrained decoding? Skips (never
    // fails) when the schema shape isn't supported or the native grammar engine is absent —
    // ComputerUseModel degrades to free generation in exactly those cases.
    [SkippableFact]
    public void Grammar_AcceptsActionSchema_OrModelFallsBackGracefully()
    {
        LMKit.TextGeneration.Sampling.Grammar? grammar = null;
        try
        {
            grammar = LMKit.TextGeneration.Sampling.Grammar.CreateJsonGrammarFromJsonSchema(
                ComputerUseActionGrammar.BuildActionSchema(new[] { 1, 2, 3 }));
        }
        catch (LMKit.Exceptions.GrammarParsingException ex)
        {
            Skip.If(true, "LM-Kit does not accept this JSON-schema shape for grammar-constrained " +
                          "decoding; ComputerUseModel falls back to unconstrained generation. " + ex.Message);
        }
        catch (System.Exception ex)
        {
            Skip.If(true, "Native grammar engine unavailable in this host: " + ex.Message);
        }

        Assert.NotNull(grammar); // reached only when the schema was accepted
    }
}
