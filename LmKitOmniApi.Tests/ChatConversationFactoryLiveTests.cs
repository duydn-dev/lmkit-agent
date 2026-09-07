using LMKit.Model;
using LMKit.TextGeneration.Chat;
using LmKitOmniApi.Infrastructure.AI;
using Xunit;

namespace LmKitOmniApi.Tests;

/// <summary>
/// LIVE proof that <see cref="ChatConversationFactory"/> delivers the system prompt on a
/// non-empty history — the one test that would catch a future LM-Kit release changing the
/// <c>MessageCount == 0</c> rule this seam is built around.
///
/// <para>
/// OPT-IN. Skips unless BOTH <c>LMKIT_LIVE_SEAM_TEST=1</c> is set AND a GGUF is reachable
/// (<c>LMKIT_LIVE_SEAM_MODEL</c>, else the LM-Kit user model cache). Loading weights takes
/// tens of seconds and multiple gigabytes of RAM, so it must never run in the normal suite.
/// </para>
/// <para>
/// Reproduced manually against Llama-3.2-1B-Instruct-Q4_K_M on 2026-09-07: the assertion below
/// FAILS on <c>new MultiTurnConversation(model, history) { SystemPrompt = ... }</c> and PASSES
/// through the factory.
/// </para>
/// </summary>
public class ChatConversationFactoryLiveTests
{
    private const string Marker = "MARKER_PERSONA_LIVE";

    private const string Persona =
        Marker + ". You must answer every user message with exactly the single word: OMNI";

    private static string? LocateModel()
    {
        if (Environment.GetEnvironmentVariable("LMKIT_LIVE_SEAM_TEST") != "1") return null;

        var explicitPath = Environment.GetEnvironmentVariable("LMKIT_LIVE_SEAM_MODEL");
        if (!string.IsNullOrWhiteSpace(explicitPath))
            return File.Exists(explicitPath) ? explicitPath : null;

        var cache = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "models", "lm-kit");

        return Directory.Exists(cache)
            ? Directory.EnumerateFiles(cache, "*.gguf", SearchOption.AllDirectories).FirstOrDefault()
            : null;
    }

    [SkippableFact]
    public void NonEmptyHistory_SystemPromptReachesTheRenderedPrompt()
    {
        var modelPath = LocateModel();
        Skip.If(modelPath is null, "Set LMKIT_LIVE_SEAM_TEST=1 with a local GGUF to run the live seam proof.");

        var model = new LM(new Uri(modelPath!));

        var history = new ChatHistory(model);
        history.AddMessage(AuthorRole.User, "hello");
        history.AddMessage(AuthorRole.Assistant, "Hi there!");

        using var chat = ChatConversationFactory.Create(model, history, Persona);
        chat.MaximumCompletionTokens = 12;
        chat.Submit("What is the capital of France?", CancellationToken.None);

        // ToText() is the literal token-side prompt, so this is the assertion that matters:
        // reading back chat.SystemPrompt would pass even when the prompt is silently dropped.
        var rendered = chat.ChatHistory.ToText();

        Assert.Contains(Marker, rendered, StringComparison.Ordinal);
        Assert.Equal(AuthorRole.System, chat.ChatHistory.Messages[0].AuthorRole);
        Assert.Single(chat.ChatHistory.Messages, m => m.AuthorRole == AuthorRole.System);
    }
}
