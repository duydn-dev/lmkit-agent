using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using LmKitOmniApi.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LmKitOmniApi.Tests;

/// <summary>
/// End-to-end, real model, real HTTP: an agent run that the inference admission queue turns
/// away must be recorded as <c>Failed</c> with the reason -- not as <c>Completed</c> with the
/// overload notice stored as its answer.
///
/// <para><b>The defect.</b> The queue reports a refusal to the orchestrator, which -- for a chat
/// turn, where a person is reading the stream -- yields the notice as text and ends the stream
/// normally. An agent run's handler has no such person: it records whatever the enumeration ends
/// with as <c>AgentRun.Result</c> and, because the enumeration ended without an exception, marks
/// the run <c>Completed</c>. So under load the user saw a green "Hoàn tất" pill whose result was
/// "hệ thống đang bận". The orchestrator now throws for run callers.</para>
///
/// <para>The single chat permit is held by the test itself, which is what makes the refusal
/// deterministic: with <c>MaxWaitSeconds = 1</c> the run's admission times out in about a second
/// and nothing depends on model timing. OPT-IN behind <c>LMKIT_LIVE_SEAM_TEST=1</c>, because the
/// handler builds its <c>ChatHistory</c> from the loaded chat model before it ever reaches the
/// queue, and CI has no weights.</para>
/// </summary>
[Collection(LiveModelCollection.Name)]
public sealed class AgentRunCapacityRejectionLiveTests
{
    private static readonly Regex RunIdMarker = new(@"\[AGENT_RUN:([0-9a-fA-F-]{36})\]", RegexOptions.Compiled);

    private static string? LocateModel()
    {
        if (Environment.GetEnvironmentVariable("LMKIT_LIVE_SEAM_TEST") != "1") return null;

        var explicitPath = Environment.GetEnvironmentVariable("LMKIT_LIVE_SEAM_MODEL");
        if (!string.IsNullOrWhiteSpace(explicitPath))
            return File.Exists(explicitPath) ? explicitPath : null;

        var cache = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "models", "lm-kit");
        if (!Directory.Exists(cache)) return null;

        return Directory.EnumerateFiles(cache, "*.gguf", SearchOption.AllDirectories)
            .Select(path => new FileInfo(path))
            .Where(file => file.Length > 1024 * 1024)
            .OrderByDescending(file => file.Length)
            .FirstOrDefault()?.FullName;
    }

    [SkippableFact]
    public async Task AnAgentRunTurnedAwayByTheInferenceQueue_IsFailed_NotCompleted()
    {
        var modelPath = LocateModel();
        Skip.If(modelPath is null, "Set LMKIT_LIVE_SEAM_TEST=1 with a local GGUF to run the live capacity proof.");

        var factory = new LmKitApiFactory();
        try
        {
            factory.ConfigurationOverrides["AiModels:DefaultChat"] = "live";
            factory.ConfigurationOverrides["AiModels:Models:live:Path"] = modelPath!;
            factory.ConfigurationOverrides["InferenceQueue:Chat:MaxWaitSeconds"] = "1";
            factory.ConfigurationOverrides["InferenceQueue:Chat:NoticeAfterMilliseconds"] = "100";
            factory.EnsureSeeded();

            // Occupy the deployment's one chat permit for the whole request, so the run's own
            // admission can only end in a refusal.
            var manager = factory.Services.GetRequiredService<LmModelManager>();
            await using var heldPermit = await manager.AcquireChatInferenceAsync();

            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
                HandleCookies = true
            });
            client.Timeout = TimeSpan.FromMinutes(10);
            var login = await client.PostAsJsonAsync("/api/auth/login", new
            {
                email = LmKitApiFactory.Email,
                password = LmKitApiFactory.Password
            });
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);

            var response = await client.PostAsJsonAsync("/api/agent-runs", new { goal = "Liệt kê ba thủ đô ở châu Âu." });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var stream = await response.Content.ReadAsStringAsync();

            var idMatch = RunIdMarker.Match(stream);
            Assert.True(idMatch.Success, $"no [AGENT_RUN:id] marker in the stream: {stream}");
            var runId = idMatch.Groups[1].Value;

            var detail = await client.GetFromJsonAsync<JsonElement>($"/api/agent-runs/{runId}");

            // The whole point. Completed here means the overload notice became the answer.
            Assert.Equal("Failed", detail.GetProperty("status").GetString());

            var error = detail.TryGetProperty("error", out var e) ? e.GetString() : null;
            Assert.False(string.IsNullOrWhiteSpace(error), "a refused run must say why it stopped");

            // And the person watching the stream was told the same thing, not a generic error.
            Assert.DoesNotContain("[ERROR]", stream, StringComparison.Ordinal);
        }
        finally
        {
            factory.Dispose();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
    }
}
