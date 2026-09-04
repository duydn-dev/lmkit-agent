using LmKitOmniApi.Infrastructure.AI.ComputerUse;
using LmKitOmniApi.Infrastructure.AI.Security;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LmKitOmniApi.Tests;

/// <summary>
/// Unit tests for the <see cref="ComputerUseAgent"/> loop with a FAKE browser executor, a
/// SCRIPTED model, and a SCRIPTED approver — no container, no model load, no database.
/// They pin the safety-critical control flow: observe→decide→(approval)→act→terminate on
/// <c>done</c>; the step cap; refusal of non-allowlisted navigation; that an unapproved
/// side-effecting action is NOT executed; and that a credential/CAPTCHA action is handed
/// off to a human, never executed.
/// </summary>
public class ComputerUseAgentTests
{
    private static readonly Guid TenantId = Guid.Parse("cccccccc-1111-1111-1111-111111111111");
    private static readonly Guid UserId = Guid.Parse("dddddddd-2222-2222-2222-222222222222");

    // ── Fakes ──

    private sealed class FakeExecutor : IComputerUseExecutor
    {
        private readonly ComputerUseObservation _observation;
        public FakeExecutor(ComputerUseObservation? observation = null)
            => _observation = observation ?? new ComputerUseObservation { Url = "about:blank", Title = "blank" };

        public bool IsEnabled { get; set; } = true;
        public List<ComputerUseActionType> Actions { get; } = new();

        public Task<ComputerUseObservation> StepAsync(
            ComputerUseAction action, Guid tenantId, Guid userId, string sessionDirectory, CancellationToken ct)
        {
            Actions.Add(action.Type);
            return Task.FromResult(_observation);
        }
    }

    private sealed class FakeModel : IComputerUseModel
    {
        private readonly Queue<string> _responses;
        private readonly string _fallback;
        public FakeModel(IEnumerable<string> responses, string? fallback = null)
        {
            _responses = new Queue<string>(responses);
            _fallback = fallback ?? "{\"action\":\"done\",\"summary\":\"done\"}";
        }

        public int CallCount { get; private set; }

        public Task<string> DecideNextActionAsync(ComputerUsePrompt prompt, CancellationToken ct)
        {
            CallCount++;
            return Task.FromResult(_responses.Count > 0 ? _responses.Dequeue() : _fallback);
        }
    }

    private sealed class FakeApprovalGate : IComputerUseApprovalGate
    {
        private readonly bool _decision;
        public FakeApprovalGate(bool decision) => _decision = decision;
        public int CallCount { get; private set; }
        public List<ComputerUseApprovalRequest> Requests { get; } = new();

        public Task<bool> RequestAsync(ComputerUseApprovalRequest request, CancellationToken ct)
        {
            CallCount++;
            Requests.Add(request);
            return Task.FromResult(_decision);
        }
    }

    private static ComputerUseOptions Options_(Action<ComputerUseOptions>? tweak = null)
    {
        var o = new ComputerUseOptions
        {
            Enabled = true,
            Image = "computer-use/browser:latest",
            MaxSteps = 15,
            StepTimeoutSeconds = 30,
            SessionWallClockSeconds = 300,
            RequireApprovalPerAction = true,
            AllowedHosts = new List<string> { "example.com" },
        };
        tweak?.Invoke(o);
        return o;
    }

    private static UserResourceAccessService Resources() =>
        new(new ToolSandboxService(NullLogger<ToolSandboxService>.Instance));

    private static ComputerUseAgent CreateAgent(
        FakeExecutor executor, FakeModel model, FakeApprovalGate gate, ComputerUseOptions options) =>
        new(executor, model, gate, Options.Create(options), Resources(),
            NullLogger<ComputerUseAgent>.Instance, audit: null);

    private static ComputerUseRequest Request(string task = "do the task", string startUrl = "") =>
        new(TenantId, UserId, Guid.NewGuid(), "User", task, startUrl);

    private static async Task<List<string>> CollectAsync(IAsyncEnumerable<string> stream)
    {
        var list = new List<string>();
        await foreach (var s in stream) list.Add(s);
        return list;
    }

    private static ComputerUseObservation WithElements(params InteractiveElement[] els) =>
        new() { Url = "https://example.com/", Title = "Login", Elements = els };

    // ── 1. observe → decide → approval → act → terminate on done ──

    [Fact]
    public async Task Loop_Observes_Decides_Approves_Acts_ThenTerminatesOnDone()
    {
        var executor = new FakeExecutor();
        var model = new FakeModel(new[]
        {
            "{\"action\":\"click\",\"ref\":1}",
            "{\"action\":\"done\",\"summary\":\"finished the task\"}",
        });
        var gate = new FakeApprovalGate(decision: true);
        var agent = CreateAgent(executor, model, gate, Options_());

        var output = await CollectAsync(agent.RunAsync(Request(), default));

        // Approval was requested once (for the click) and the click executed after it.
        Assert.Equal(1, gate.CallCount);
        Assert.Equal(new[] { ComputerUseActionType.Click }, executor.Actions);
        Assert.Equal(2, model.CallCount);

        // The HITL marker was emitted, and it came BEFORE the click's STEP marker.
        var hitlIndex = output.FindIndex(s => s.StartsWith("[HITL_APPROVAL_REQUIRED:", StringComparison.Ordinal));
        var stepIndex = output.FindIndex(s => s.StartsWith("[STEP:", StringComparison.Ordinal));
        Assert.True(hitlIndex >= 0, "expected a HITL_APPROVAL_REQUIRED marker");
        Assert.True(stepIndex > hitlIndex, "the executed step must follow the approval");

        // The run correlates a session and ends with the done summary.
        Assert.StartsWith("[COMPUTER_USE:", output[0]);
        Assert.Contains(output, s => s.Contains("finished the task"));
    }

    // ── 2. step cap ──

    [Fact]
    public async Task Loop_EnforcesStepCap()
    {
        var executor = new FakeExecutor();
        // The model never says "done" — it always scrolls (read-only, so no approval).
        var model = new FakeModel(Array.Empty<string>(), fallback: "{\"action\":\"scroll\",\"direction\":\"down\",\"amount\":3}");
        var gate = new FakeApprovalGate(decision: true);
        var agent = CreateAgent(executor, model, gate, Options_(o => o.MaxSteps = 3));

        var output = await CollectAsync(agent.RunAsync(Request(), default));

        Assert.Equal(3, executor.Actions.Count);
        Assert.All(executor.Actions, a => Assert.Equal(ComputerUseActionType.Scroll, a));
        Assert.Equal(0, gate.CallCount); // scroll is read-only → never gated
        Assert.Contains(output, s => s.Contains("giới hạn 3 bước"));
    }

    // ── 3. non-allowlisted navigation refused (before approval, before executor) ──

    [Fact]
    public async Task Loop_RefusesNavigationToNonAllowlistedHost()
    {
        var executor = new FakeExecutor();
        var model = new FakeModel(new[]
        {
            "{\"action\":\"navigate\",\"url\":\"http://evil.example.net/\"}",
            "{\"action\":\"done\",\"summary\":\"stopping\"}",
        });
        var gate = new FakeApprovalGate(decision: true);
        var agent = CreateAgent(executor, model, gate, Options_(o => o.AllowedHosts = new List<string> { "example.com" }));

        var output = await CollectAsync(agent.RunAsync(Request(), default));

        Assert.DoesNotContain(ComputerUseActionType.Navigate, executor.Actions); // never navigated
        Assert.Equal(0, gate.CallCount);                                          // never even asked for approval
        Assert.Contains(output, s => s.Contains("không được phép"));
    }

    // ── 4. unapproved side-effecting action is NOT executed ──

    [Fact]
    public async Task Loop_SideEffectingAction_NotApproved_IsNotExecuted()
    {
        var executor = new FakeExecutor();
        var model = new FakeModel(new[] { "{\"action\":\"click\",\"ref\":1}" });
        var gate = new FakeApprovalGate(decision: false); // human rejects
        var agent = CreateAgent(executor, model, gate, Options_());

        var output = await CollectAsync(agent.RunAsync(Request(), default));

        Assert.Equal(1, gate.CallCount);
        Assert.Empty(executor.Actions); // the click never executed
        Assert.Contains(output, s => s.StartsWith("[HITL_APPROVAL_REQUIRED:", StringComparison.Ordinal));
        Assert.Contains(output, s => s.Contains("không được phê duyệt"));
    }

    // ── 5. credential field → hand off to a human, never typed, never gated ──

    [Fact]
    public async Task Loop_CredentialField_IsHandedOff_NeverTyped()
    {
        var executor = new FakeExecutor(WithElements(
            new InteractiveElement(1, "textbox", "Username", null),
            new InteractiveElement(2, "textbox", "Password", null)));
        var model = new FakeModel(new[]
        {
            "{\"action\":\"screenshot\"}",                    // observe → reveals the password field
            "{\"action\":\"type\",\"ref\":2,\"text\":\"hunter2\"}", // then tries to type into it
        });
        var gate = new FakeApprovalGate(decision: true);
        var agent = CreateAgent(executor, model, gate, Options_());

        var output = await CollectAsync(agent.RunAsync(Request(), default));

        // Only the read-only screenshot executed; the credential type was refused.
        Assert.Equal(new[] { ComputerUseActionType.Screenshot }, executor.Actions);
        Assert.DoesNotContain(ComputerUseActionType.Type, executor.Actions);
        Assert.Equal(0, gate.CallCount); // refused outright — never routed to approval
        Assert.Contains(output, s => s.Contains("🛑"));
    }

    // ── 6. CAPTCHA click → hand off, never clicked ──

    [Fact]
    public async Task Loop_Captcha_IsHandedOff_NeverClicked()
    {
        var executor = new FakeExecutor(WithElements(
            new InteractiveElement(3, "button", "reCAPTCHA - verify you are human", null)));
        var model = new FakeModel(new[]
        {
            "{\"action\":\"screenshot\"}",
            "{\"action\":\"click\",\"ref\":3}",
        });
        var gate = new FakeApprovalGate(decision: true);
        var agent = CreateAgent(executor, model, gate, Options_());

        var output = await CollectAsync(agent.RunAsync(Request(), default));

        Assert.Equal(new[] { ComputerUseActionType.Screenshot }, executor.Actions);
        Assert.DoesNotContain(ComputerUseActionType.Click, executor.Actions);
        Assert.Equal(0, gate.CallCount);
        Assert.Contains(output, s => s.Contains("🛑"));
    }

    // ── 7. disabled agent never loops ──

    [Fact]
    public async Task Disabled_Agent_YieldsNotEnabled_AndDoesNotLoop()
    {
        var executor = new FakeExecutor { IsEnabled = false };
        var model = new FakeModel(new[] { "{\"action\":\"done\",\"summary\":\"x\"}" });
        var gate = new FakeApprovalGate(decision: true);
        var agent = CreateAgent(executor, model, gate, Options_(o => o.Enabled = false));

        Assert.False(agent.IsEnabled);
        var output = await CollectAsync(agent.RunAsync(Request(), default));

        Assert.Empty(executor.Actions);
        Assert.Equal(0, model.CallCount);
        Assert.Contains(output, s => s.Contains("chưa được bật"));
    }
}
