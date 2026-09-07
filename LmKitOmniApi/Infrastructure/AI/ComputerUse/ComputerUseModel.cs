using System.Linq;
using System.Text;
using LMKit.TextGeneration;
using LMKit.TextGeneration.Chat;
using LMKit.TextGeneration.Sampling;
using LmKitOmniApi.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LmKitOmniApi.Infrastructure.AI.ComputerUse;

/// <summary>
/// Default <see cref="IComputerUseModel"/>. Renders the observation into a compact,
/// accessibility-grounded text block (url, title, and the numbered element list the
/// model addresses by ref), attaches the current screenshot, and asks the VISION model
/// for exactly one next action via <c>LmModelManager</c> (GetVisionModelAsync +
/// AcquireVisionInferenceAsync — the same acquire-lease discipline the rest of the app
/// uses). LIVE-ONLY: it needs a loaded vision model, so it is exercised in the running
/// stack, not CI (the loop tests inject a scripted <see cref="IComputerUseModel"/>).
/// </summary>
public sealed class ComputerUseModel : IComputerUseModel
{
    private const int MaxCompletionTokens = 512;

    private readonly LmModelManager _modelManager;
    private readonly ComputerUseOptions _options;
    private readonly ILogger<ComputerUseModel> _logger;

    public ComputerUseModel(LmModelManager modelManager, IOptions<ComputerUseOptions> options, ILogger<ComputerUseModel> logger)
    {
        _modelManager = modelManager;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>
    /// Asks the vision model for exactly one next action.
    ///
    /// EVERY native handle opened here is disposed on the way out. The conversation, the
    /// grammar and the screenshot attachment are all <see cref="IDisposable"/>, and this
    /// method runs once per step, once per grounding retry (up to
    /// <c>ComputerUse:GroundingRetries</c> + 1) and once per grounding-eval case (a batch can
    /// be hundreds) — leaking two or three handles per call exhausted native resources long
    /// before a run finished. Generation uses <c>SubmitAsync</c> so the request thread is
    /// never blocked on the native call.
    /// </summary>
    public async Task<string> DecideNextActionAsync(ComputerUsePrompt prompt, CancellationToken ct = default)
    {
        var visionModel = await _modelManager.GetVisionModelAsync(ct: ct);
        await using var lease = await _modelManager.AcquireVisionInferenceAsync(ct);

        using var chat = new MultiTurnConversation(visionModel)
        {
            SystemPrompt = prompt.SystemPrompt,
            MaximumCompletionTokens = MaxCompletionTokens,
        };

        // Grounding hardening: constrain generation to the action schema (and, when the page has
        // elements, to the REAL ref set) so a malformed / hallucinated-ref action cannot even be
        // sampled. Fail-safe — if LM-Kit rejects the schema, fall back to free generation (the
        // loop's self-correction retry + fail-closed grounding gate still protect).
        // `using` on a null Grammar is a no-op, so the fallback path allocates nothing.
        using var grammar = TryBuildGrammar(prompt);
        if (grammar is not null) chat.Grammar = grammar;

        var userText = BuildUserMessage(prompt);

        // Use only the LM-Kit overloads the rest of the app already relies on: a
        // (text, attachment) Message for the vision turn, or a plain text submit when no
        // screenshot was captured.
        string? completion;
        if (!string.IsNullOrEmpty(prompt.ScreenshotPath) && System.IO.File.Exists(prompt.ScreenshotPath))
        {
            using var attachment = new LMKit.Data.Attachment(prompt.ScreenshotPath);
            var message = new ChatHistory.Message(userText, attachment);
            completion = (await chat.SubmitAsync(message, ct)).Completion;
        }
        else
        {
            completion = (await chat.SubmitAsync(userText, ct)).Completion;
        }

        _logger.LogInformation("🧠 [ComputerUse] Mô hình đề xuất hành động tiếp theo ({Chars} ký tự).",
            completion?.Length ?? 0);
        return completion ?? string.Empty;
    }

    /// <summary>
    /// Builds the constrained-decoding grammar, or null when constrained decoding is off or
    /// LM-Kit rejects the schema. The caller owns (and disposes) the returned handle.
    /// </summary>
    private Grammar? TryBuildGrammar(ComputerUsePrompt prompt)
    {
        if (!_options.ConstrainedDecoding) return null;
        try
        {
            var refs = prompt.Observation.Elements.Select(element => element.Ref).ToList();
            return Grammar.CreateJsonGrammarFromJsonSchema(ComputerUseActionGrammar.BuildActionSchema(refs));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "⚠️ [ComputerUse] Không dựng được grammar ràng buộc — sinh tự do (retry + grounding gate vẫn bảo vệ).");
            return null;
        }
    }

    /// <summary>Renders the task, prior-step history, and the current observation into the user turn.</summary>
    private static string BuildUserMessage(ComputerUsePrompt prompt)
    {
        var sb = new StringBuilder();
        sb.Append("TASK: ").Append(prompt.TaskGoal).Append('\n');

        if (prompt.History.Count > 0)
        {
            sb.Append("\nHISTORY (most recent last):\n");
            foreach (var line in prompt.History)
                sb.Append("- ").Append(line).Append('\n');
        }

        var obs = prompt.Observation;
        sb.Append("\nCURRENT PAGE:\n");
        sb.Append("  url: ").Append(obs.Url).Append('\n');
        sb.Append("  title: ").Append(obs.Title).Append('\n');
        if (obs.IsError)
            sb.Append("  note: previous step reported: ").Append(obs.Error).Append('\n');

        sb.Append("\nINTERACTIVE ELEMENTS (address these by 'ref'):\n");
        if (obs.Elements.Count == 0)
        {
            sb.Append("  (none detected)\n");
        }
        else
        {
            foreach (var el in obs.Elements)
            {
                sb.Append("  [").Append(el.Ref).Append("] ").Append(el.Role).Append(": ").Append(el.Name);
                if (!string.IsNullOrEmpty(el.Value)) sb.Append(" = \"").Append(el.Value).Append('"');
                sb.Append('\n');
            }
        }

        sb.Append("\nA screenshot of this page is attached. Respond with EXACTLY ONE action as JSON.");
        return sb.ToString();
    }
}
