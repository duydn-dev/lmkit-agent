using System.Text;
using LMKit.TextGeneration;
using LMKit.TextGeneration.Chat;
using LmKitOmniApi.Services;
using Microsoft.Extensions.Logging;

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
    private readonly ILogger<ComputerUseModel> _logger;

    public ComputerUseModel(LmModelManager modelManager, ILogger<ComputerUseModel> logger)
    {
        _modelManager = modelManager;
        _logger = logger;
    }

    public async Task<string> DecideNextActionAsync(ComputerUsePrompt prompt, CancellationToken ct = default)
    {
        var visionModel = await _modelManager.GetVisionModelAsync(ct: ct);
        await using var lease = await _modelManager.AcquireVisionInferenceAsync(ct);

        var chat = new MultiTurnConversation(visionModel)
        {
            SystemPrompt = prompt.SystemPrompt,
            MaximumCompletionTokens = MaxCompletionTokens,
        };

        var userText = BuildUserMessage(prompt);

        // Use only the LM-Kit overloads the rest of the app already relies on: a
        // (text, attachment) Message for the vision turn, or a plain text Submit when no
        // screenshot was captured.
        string? completion;
        if (!string.IsNullOrEmpty(prompt.ScreenshotPath) && System.IO.File.Exists(prompt.ScreenshotPath))
        {
            var attachment = new LMKit.Data.Attachment(prompt.ScreenshotPath);
            var message = new ChatHistory.Message(userText, attachment);
            completion = chat.Submit(message, ct).Completion;
        }
        else
        {
            completion = chat.Submit(userText, ct).Completion;
        }

        _logger.LogInformation("🧠 [ComputerUse] Mô hình đề xuất hành động tiếp theo ({Chars} ký tự).",
            completion?.Length ?? 0);
        return completion ?? string.Empty;
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
