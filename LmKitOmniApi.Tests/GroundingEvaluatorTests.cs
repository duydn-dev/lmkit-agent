using LmKitOmniApi.Infrastructure.AI.ComputerUse;
using LmKitOmniApi.Infrastructure.AI.ComputerUse.Eval;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LmKitOmniApi.Tests;

/// <summary>
/// Unit tests for the <see cref="GroundingEvaluator"/> harness with a SCRIPTED
/// <see cref="IComputerUseModel"/> that returns canned next-action JSON per case — no model
/// load, no browser, no DB. They pin the outcome CLASSIFICATION (Correct / ValidButWrong /
/// Hallucinated / NonElement / Malformed), the aggregate metrics
/// (<c>GroundingAccuracy</c> / <c>HallucinationRate</c>) over a mixed batch, and that the
/// harness REFUSES while disabled.
/// </summary>
public class GroundingEvaluatorTests
{
    // ── Scripted model: one canned response per call (FIFO), in case order ──
    private sealed class ScriptedModel : IComputerUseModel
    {
        private readonly Queue<string> _responses;
        public ScriptedModel(params string[] responses) => _responses = new Queue<string>(responses);

        public int CallCount { get; private set; }
        public List<ComputerUsePrompt> Prompts { get; } = new();

        public Task<string> DecideNextActionAsync(ComputerUsePrompt prompt, CancellationToken ct = default)
        {
            CallCount++;
            Prompts.Add(prompt);
            return Task.FromResult(_responses.Count > 0 ? _responses.Dequeue() : "{\"action\":\"done\",\"summary\":\"fallback\"}");
        }
    }

    private sealed class ThrowingModel : IComputerUseModel
    {
        public Task<string> DecideNextActionAsync(ComputerUsePrompt prompt, CancellationToken ct = default)
            => throw new InvalidOperationException("model blew up");
    }

    private static GroundingEvaluator Evaluator(IComputerUseModel model, bool enabled = true) =>
        new(model, Options.Create(new GroundingEvalOptions { Enabled = enabled }),
            NullLogger<GroundingEvaluator>.Instance);

    // A page exposing refs 1 (button OK), 2 (link Cancel), 3 (textbox Search).
    private static ComputerUseObservation Page() => new()
    {
        Url = "https://example.com/",
        Title = "Sample",
        Elements = new[]
        {
            new InteractiveElement(1, "button", "OK", null),
            new InteractiveElement(2, "link", "Cancel", null),
            new InteractiveElement(3, "textbox", "Search", null),
        },
    };

    private static GroundingEvalCase Case(int expected, IReadOnlyList<int>? acceptable = null) =>
        new("do the thing", Page(), expected, acceptable);

    // ── 1. correct ref → Correct ──

    [Fact]
    public async Task Classifies_CorrectRef_AsCorrect()
    {
        var model = new ScriptedModel("{\"action\":\"click\",\"ref\":1}");
        var report = await Evaluator(model).EvaluateAsync(new[] { Case(expected: 1) });

        Assert.Equal(1, report.Total);
        Assert.Equal(1, report.Correct);
        Assert.Equal(GroundingOutcome.Correct, report.Cases[0].Outcome);
        Assert.Equal(1, report.Cases[0].ChosenRef);
        Assert.Equal(1.0, report.GroundingAccuracy, 3);
        Assert.Equal(0.0, report.HallucinationRate, 3);
    }

    // ── 2. real-but-wrong ref → ValidButWrong ──

    [Fact]
    public async Task Classifies_RealButUnacceptableRef_AsValidButWrong()
    {
        // ref 2 (Cancel) exists on the page but is not the expected ref (1).
        var model = new ScriptedModel("{\"action\":\"click\",\"ref\":2}");
        var report = await Evaluator(model).EvaluateAsync(new[] { Case(expected: 1) });

        Assert.Equal(1, report.ValidButWrong);
        Assert.Equal(0, report.Correct);
        Assert.Equal(GroundingOutcome.ValidButWrong, report.Cases[0].Outcome);
        Assert.Equal(2, report.Cases[0].ChosenRef);
    }

    // ── 3. ref not on the page → Hallucinated ──

    [Fact]
    public async Task Classifies_RefNotInObservation_AsHallucinated()
    {
        var model = new ScriptedModel("{\"action\":\"click\",\"ref\":99}");
        var report = await Evaluator(model).EvaluateAsync(new[] { Case(expected: 1) });

        Assert.Equal(1, report.Hallucinated);
        Assert.Equal(GroundingOutcome.Hallucinated, report.Cases[0].Outcome);
        Assert.Equal(99, report.Cases[0].ChosenRef);
        Assert.Equal(1.0, report.HallucinationRate, 3);
    }

    // ── 4. unparseable output → Malformed (with the parser's reason retained) ──

    [Fact]
    public async Task Classifies_UnparseableOutput_AsMalformed()
    {
        var model = new ScriptedModel("this is not an action at all");
        var report = await Evaluator(model).EvaluateAsync(new[] { Case(expected: 1) });

        Assert.Equal(1, report.Malformed);
        Assert.Equal(GroundingOutcome.Malformed, report.Cases[0].Outcome);
        Assert.Null(report.Cases[0].ChosenRef);
        Assert.False(string.IsNullOrWhiteSpace(report.Cases[0].ParseError));
    }

    // ── 5. parsed but not element-targeting → NonElement ──

    [Theory]
    [InlineData("{\"action\":\"done\",\"summary\":\"finished\"}")]
    [InlineData("{\"action\":\"scroll\",\"direction\":\"down\",\"amount\":3}")]
    [InlineData("{\"action\":\"navigate\",\"url\":\"https://example.com/next\"}")]
    [InlineData("{\"action\":\"type\",\"x\":100,\"y\":200,\"text\":\"hi\"}")] // coordinate-only, no ref
    public async Task Classifies_NonRefTargetingAction_AsNonElement(string raw)
    {
        var model = new ScriptedModel(raw);
        var report = await Evaluator(model).EvaluateAsync(new[] { Case(expected: 1) });

        Assert.Equal(1, report.NonElement);
        Assert.Equal(GroundingOutcome.NonElement, report.Cases[0].Outcome);
        Assert.Null(report.Cases[0].ChosenRef);
    }

    // ── 6. AcceptableRefs widens "correct" to a set ──

    [Fact]
    public async Task AcceptableRefs_TreatsAnyListedRef_AsCorrect()
    {
        // Expected 5, but 9 is also acceptable and on the page → picking 9 is Correct.
        var obs = new ComputerUseObservation
        {
            Url = "https://example.com/p",
            Title = "Product",
            Elements = new[]
            {
                new InteractiveElement(5, "button", "Add to cart", null),
                new InteractiveElement(9, "button", "Add to cart", null),
            },
        };
        var model = new ScriptedModel("{\"action\":\"click\",\"ref\":9}");
        var report = await Evaluator(model).EvaluateAsync(new[]
        {
            new GroundingEvalCase("add to cart", obs, ExpectedRef: 5, AcceptableRefs: new[] { 5, 9 }),
        });

        Assert.Equal(1, report.Correct);
        Assert.Equal(GroundingOutcome.Correct, report.Cases[0].Outcome);
    }

    // ── 7. mixed batch: counts + GroundingAccuracy + HallucinationRate ──

    [Fact]
    public async Task MixedBatch_ComputesCountsAndRatesCorrectly()
    {
        // 5 cases: Correct, Correct, Hallucinated, ValidButWrong, Malformed.
        var model = new ScriptedModel(
            "{\"action\":\"click\",\"ref\":1}",   // Correct
            "{\"action\":\"click\",\"ref\":1}",   // Correct
            "{\"action\":\"click\",\"ref\":42}",  // Hallucinated (42 not on page)
            "{\"action\":\"click\",\"ref\":2}",   // ValidButWrong (2 exists, expected 1)
            "garbage output");                     // Malformed

        var cases = new[] { Case(1), Case(1), Case(1), Case(1), Case(1) };
        var report = await Evaluator(model).EvaluateAsync(cases);

        Assert.Equal(5, report.Total);
        Assert.Equal(2, report.Correct);
        Assert.Equal(1, report.Hallucinated);
        Assert.Equal(1, report.ValidButWrong);
        Assert.Equal(1, report.Malformed);
        Assert.Equal(0, report.NonElement);

        Assert.Equal(0.4, report.GroundingAccuracy, 3); // 2 / 5
        Assert.Equal(0.2, report.HallucinationRate, 3); // 1 / 5
        Assert.Equal(5, report.Cases.Count);
        Assert.Equal(5, model.CallCount);
    }

    // ── 8. disabled → refuses (throws), never calls the model ──

    [Fact]
    public async Task Disabled_Refuses_AndDoesNotCallModel()
    {
        var model = new ScriptedModel("{\"action\":\"click\",\"ref\":1}");
        var evaluator = Evaluator(model, enabled: false);

        Assert.False(evaluator.IsEnabled);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => evaluator.EvaluateAsync(new[] { Case(expected: 1) }));
        Assert.Equal(0, model.CallCount);
    }

    // ── 9. enabled flag mirrors options ──

    [Fact]
    public void IsEnabled_MirrorsOptions()
    {
        Assert.True(Evaluator(new ScriptedModel(), enabled: true).IsEnabled);
        Assert.False(Evaluator(new ScriptedModel(), enabled: false).IsEnabled);
    }

    // ── 10. empty batch → all-zero report, ratios are 0 (not NaN) ──

    [Fact]
    public async Task EmptyBatch_YieldsZeroReport_WithZeroRates()
    {
        var report = await Evaluator(new ScriptedModel()).EvaluateAsync(Array.Empty<GroundingEvalCase>());

        Assert.Equal(0, report.Total);
        Assert.Empty(report.Cases);
        Assert.Equal(0.0, report.GroundingAccuracy, 3);
        Assert.Equal(0.0, report.HallucinationRate, 3);
    }

    // ── 11. null cases → ArgumentNullException ──

    [Fact]
    public async Task NullCases_Throws()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => Evaluator(new ScriptedModel()).EvaluateAsync(null!));
    }

    // ── 12. a per-case model failure is contained as Malformed, batch still completes ──

    [Fact]
    public async Task ModelException_IsContained_AsMalformed()
    {
        var report = await Evaluator(new ThrowingModel()).EvaluateAsync(new[] { Case(expected: 1) });

        Assert.Equal(1, report.Total);
        Assert.Equal(1, report.Malformed);
        Assert.Equal(GroundingOutcome.Malformed, report.Cases[0].Outcome);
        Assert.Contains("model error", report.Cases[0].ParseError);
    }

    // ── 13. the prompt reuses ComputerUseAgent.SystemPrompt and the case's observation ──

    [Fact]
    public async Task Prompt_ReusesSystemPrompt_AndCaseObservationAndGoal()
    {
        var model = new ScriptedModel("{\"action\":\"click\",\"ref\":1}");
        var theCase = Case(expected: 1);
        await Evaluator(model).EvaluateAsync(new[] { theCase });

        var prompt = Assert.Single(model.Prompts);
        Assert.Same(ComputerUseAgent.SystemPrompt, prompt.SystemPrompt);
        Assert.Equal(theCase.TaskGoal, prompt.TaskGoal);
        Assert.Same(theCase.Observation, prompt.Observation);
        Assert.Null(prompt.ScreenshotPath);
    }

    // ── 14. tolerant parsing flows through (fenced JSON with prose → Correct) ──

    [Fact]
    public async Task TolerantParsing_FencedJsonWithProse_StillClassifies()
    {
        var model = new ScriptedModel("Sure! Here is my action:\n```json\n{\"action\":\"click\",\"ref\":1}\n```");
        var report = await Evaluator(model).EvaluateAsync(new[] { Case(expected: 1) });

        Assert.Equal(GroundingOutcome.Correct, report.Cases[0].Outcome);
    }

    // ── 15. Classify() static helper — direct, exhaustive ──

    [Fact]
    public void Classify_CoversEveryBucket()
    {
        var obs = Page();
        var acceptable = new[] { 1 };

        Assert.Equal(GroundingOutcome.Malformed,
            GroundingEvaluator.Classify(parsed: false, action: null, obs, acceptable));

        Assert.Equal(GroundingOutcome.NonElement,
            GroundingEvaluator.Classify(true, new ComputerUseAction { Type = ComputerUseActionType.Done }, obs, acceptable));

        Assert.Equal(GroundingOutcome.Hallucinated,
            GroundingEvaluator.Classify(true, new ComputerUseAction { Type = ComputerUseActionType.Click, Ref = 99 }, obs, acceptable));

        Assert.Equal(GroundingOutcome.ValidButWrong,
            GroundingEvaluator.Classify(true, new ComputerUseAction { Type = ComputerUseActionType.Click, Ref = 2 }, obs, acceptable));

        Assert.Equal(GroundingOutcome.Correct,
            GroundingEvaluator.Classify(true, new ComputerUseAction { Type = ComputerUseActionType.Click, Ref = 1 }, obs, acceptable));
    }
}
