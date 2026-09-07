using System.Linq;
using System.Text;
using LMKit.Model;
using LMKit.TextGeneration;
using LMKit.TextGeneration.Chat;
using LMKit.TextGeneration.Sampling;
using LmKitOmniApi.Infrastructure.AI.ComputerUse.Training;
using LmKitOmniApi.Infrastructure.AI.Lora;
using LmKitOmniApi.Services;
using Microsoft.AspNetCore.Http;
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
///
/// <para>
/// GROUNDING ADAPTER. This is the model the grounding LoRA pipeline trains
/// (<see cref="LmKitGroundingAdapterTrainerPort"/>), so it is also the model that can load the
/// result: when <c>GroundingTraining:ApplyTrainedAdapter</c> is on, the tenant's newest active
/// auto-trained grounding adapter is applied to the vision model for the duration of the
/// decision and removed again before the vision lease is released.
/// </para>
/// </summary>
public sealed class ComputerUseModel : IComputerUseModel
{
    private const int MaxCompletionTokens = 512;

    private readonly LmModelManager _modelManager;
    private readonly ComputerUseOptions _options;
    private readonly ILogger<ComputerUseModel> _logger;

    // Grounding-adapter hot-swap (off by default). Optional so existing call sites/tests that
    // construct this positionally keep compiling; DI fills all three by type.
    private readonly ILoraAdapterService? _loraService;
    private readonly IHttpContextAccessor? _httpContextAccessor;
    private readonly GroundingTrainingOptions _groundingOptions;

    public ComputerUseModel(
        LmModelManager modelManager,
        IOptions<ComputerUseOptions> options,
        ILogger<ComputerUseModel> logger,
        ILoraAdapterService? loraService = null,
        IHttpContextAccessor? httpContextAccessor = null,
        IOptions<GroundingTrainingOptions>? groundingOptions = null)
    {
        _modelManager = modelManager;
        _options = options.Value;
        _logger = logger;
        _loraService = loraService;
        _httpContextAccessor = httpContextAccessor;
        _groundingOptions = groundingOptions?.Value ?? new GroundingTrainingOptions();
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

        // Grounding LoRA hot-swap: apply the tenant's trained adapter to the shared vision model
        // for this one decision. Declared AFTER the lease and BEFORE the conversation, so `using`
        // disposes it in reverse order — the adapter is removed while we still hold exclusive
        // access to the model (Vision semaphore = 1), and even if generation throws. Null (a
        // no-op) whenever the feature is off or the tenant has no adapter.
        using var groundingAdapter = await TryApplyGroundingAdapterAsync(visionModel, ct);

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

        var hasScreenshot = !string.IsNullOrEmpty(prompt.ScreenshotPath) && System.IO.File.Exists(prompt.ScreenshotPath);
        var userText = BuildUserMessage(prompt, hasScreenshot);

        // Use only the LM-Kit overloads the rest of the app already relies on: a
        // (text, attachment) Message for the vision turn, or a plain text submit when no
        // screenshot was captured.
        string? completion;
        if (hasScreenshot)
        {
            using var attachment = new LMKit.Data.Attachment(prompt.ScreenshotPath!);
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

    /// <summary>
    /// Applies the tenant's newest active auto-trained grounding adapter to the vision model, or
    /// returns null (a no-op) when the feature is off, there is no request principal to read the
    /// tenant from, the LoRA feature is off, or the tenant has never trained one.
    ///
    /// The caller MUST already hold the vision inference lease, and must dispose the returned
    /// scope before releasing it — the same contract as
    /// <see cref="ILoraAdapterService.BeginApplyForAgent"/> on the chat path.
    /// </summary>
    private async Task<LoraApplyScope?> TryApplyGroundingAdapterAsync(LM visionModel, CancellationToken ct)
    {
        if (!_groundingOptions.ApplyTrainedAdapter || _loraService is null) return null;

        // The computer-use run and the grounding-eval harness are both authenticated HTTP
        // requests, so the tenant comes from the same claim every controller reads.
        var tenantClaim = _httpContextAccessor?.HttpContext?.User?.FindFirst("TenantId")?.Value;
        if (!Guid.TryParse(tenantClaim, out var tenantId)) return null;

        try
        {
            var adapterId = await GroundingAdapterNaming.SelectNewestActiveAsync(_loraService, tenantId, ct);
            if (adapterId is null) return null;

            var scope = _loraService.BeginApplyForAgent(visionModel, tenantId, adapterId, ct);
            if (scope is not null)
                _logger.LogDebug("🎯 [ComputerUse] Đã áp dụng adapter grounding {AdapterId} cho mô hình thị giác.", adapterId);
            return scope;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Selecting/applying the adapter must never break a decision; run on the base model.
            _logger.LogWarning(ex, "⚠️ [ComputerUse] Không áp dụng được adapter grounding — dùng mô hình gốc.");
            return null;
        }
    }

    /// <summary>
    /// Renders the task, prior-step history, and the current observation into the user turn.
    /// <c>internal</c> so <c>GroundingTrainingPlanTests</c> can pin the TRAINING turn
    /// (<see cref="GroundingTrainingPlan.RenderUserTurn"/>) to this exact rendering — a
    /// fine-tune whose input shape differs from the inference input teaches the wrong thing.
    /// </summary>
    internal static string BuildUserMessage(ComputerUsePrompt prompt, bool hasScreenshot)
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

        // Only claim a screenshot when one is actually attached — the grounding trainer renders
        // the same turn from the recorded sample, and telling the model about a picture it cannot
        // see is both a lie and a training/inference mismatch.
        sb.Append(hasScreenshot
            ? "\nA screenshot of this page is attached. Respond with EXACTLY ONE action as JSON."
            : "\nRespond with EXACTLY ONE action as JSON.");
        return sb.ToString();
    }
}
