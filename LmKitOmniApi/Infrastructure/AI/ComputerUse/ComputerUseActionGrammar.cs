using System.Text.Json;

namespace LmKitOmniApi.Infrastructure.AI.ComputerUse;

/// <summary>
/// Builds the JSON schema used to CONSTRAIN the model's next-action output via LM-Kit's
/// grammar-constrained decoding (<c>LMKit.TextGeneration.Sampling.Grammar</c>, set on
/// <c>MultiTurnConversation.Grammar</c>). Constraining generation to this schema makes a
/// MALFORMED action impossible to sample, and — when the current observation exposes
/// interactive elements — pins <c>ref</c> to the set of REAL element refs, so a HALLUCINATED
/// ref can never be produced in the first place. This is a stronger guarantee than the
/// after-the-fact self-correction retry in <see cref="ComputerUseAgent"/> (which recovers
/// from a bad action); together they mean the model is nudged hard toward a groundable action
/// and, failing that, the loop's fail-closed gate still refuses to act blindly.
///
/// This builder is pure + deterministic (unit-tested). The actual constrained generation
/// lives in the live-only <see cref="ComputerUseModel"/>; if LM-Kit rejects the schema at
/// runtime, that path falls back to unconstrained generation (the retry + grounding gate
/// remain), so constrained decoding is a hardening, never a hard dependency.
/// </summary>
public static class ComputerUseActionGrammar
{
    /// <summary>The nine action verbs the loop understands (mirrors <see cref="ComputerUseActionParser"/>).</summary>
    private static readonly string[] ActionTypes =
        { "navigate", "click", "type", "key", "scroll", "wait", "screenshot", "done", "ask" };

    private static readonly string[] ScrollDirections = { "up", "down", "left", "right" };

    /// <summary>
    /// JSON schema for exactly one action, in the flat <c>{"action":…, "ref":…, …}</c> shape
    /// the parser reads. When <paramref name="elementRefs"/> is non-empty, <c>ref</c> is
    /// constrained to exactly those integer values (only a real element can be addressed);
    /// when empty, <c>ref</c> is an unconstrained integer (the loop's grounding gate still
    /// refuses an un-groundable action, and the prompt tells the model no elements exist).
    /// </summary>
    public static string BuildActionSchema(IReadOnlyList<int> elementRefs)
    {
        object refSchema = elementRefs is { Count: > 0 }
            ? new Dictionary<string, object> { ["type"] = "integer", ["enum"] = elementRefs.Distinct().ToArray() }
            : new Dictionary<string, object> { ["type"] = "integer" };

        var schema = new Dictionary<string, object>
        {
            ["type"] = "object",
            ["properties"] = new Dictionary<string, object>
            {
                ["action"] = new Dictionary<string, object> { ["type"] = "string", ["enum"] = ActionTypes },
                ["ref"] = refSchema,
                ["text"] = new Dictionary<string, object> { ["type"] = "string" },
                ["keys"] = new Dictionary<string, object> { ["type"] = "string" },
                ["url"] = new Dictionary<string, object> { ["type"] = "string" },
                ["direction"] = new Dictionary<string, object> { ["type"] = "string", ["enum"] = ScrollDirections },
                ["amount"] = new Dictionary<string, object> { ["type"] = "integer" },
                ["summary"] = new Dictionary<string, object> { ["type"] = "string" },
                ["question"] = new Dictionary<string, object> { ["type"] = "string" },
            },
            ["required"] = new[] { "action" },
        };

        return JsonSerializer.Serialize(schema);
    }
}
