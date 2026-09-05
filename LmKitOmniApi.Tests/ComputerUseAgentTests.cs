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
            // Literal public IP "1.1.1.1" is allowlisted so the post-step landing re-validation
            // (GAP 2) passes WITHOUT any DNS query — Dns short-circuits an IP literal, keeping
            // these tests hermetic (same trick as ComputerUseExecutorTests).
            AllowedHosts = new List<string> { "example.com", "1.1.1.1" },
        };
        tweak?.Invoke(o);
        return o;
    }

    private static UserResourceAccessService Resources() =>
        new(new ToolSandboxService(NullLogger<ToolSandboxService>.Instance));

    private static ComputerUseAgent CreateAgent(
        FakeExecutor executor, FakeModel model, FakeApprovalGate gate, ComputerUseOptions options) =>
        new(executor, model, gate, Options.Create(options), Resources(),
            new ToolSandboxService(NullLogger<ToolSandboxService>.Instance),
            NullLogger<ComputerUseAgent>.Instance, audit: null);

    private static ComputerUseRequest Request(string task = "do the task", string startUrl = "") =>
        new(TenantId, UserId, Guid.NewGuid(), "User", task, startUrl);

    private static async Task<List<string>> CollectAsync(IAsyncEnumerable<string> stream)
    {
        var list = new List<string>();
        await foreach (var s in stream) list.Add(s);
        return list;
    }

    // A hermetic landing URL: literal IP "1.1.1.1" (allowlisted in Options_) so the GAP 2
    // landing re-validation resolves without a network query.
    private static ComputerUseObservation WithElements(params InteractiveElement[] els) =>
        new() { Url = "https://1.1.1.1/", Title = "Login", Elements = els };

    // ── 1. observe → decide → approval → act → terminate on done ──

    [Fact]
    public async Task Loop_Observes_Decides_Approves_Acts_ThenTerminatesOnDone()
    {
        // The observation must expose the element the model addresses (ref 1) so the click is
        // groundable; a read-only screenshot observes it first.
        var executor = new FakeExecutor(WithElements(new InteractiveElement(1, "button", "Continue", null)));
        var model = new FakeModel(new[]
        {
            "{\"action\":\"screenshot\"}",
            "{\"action\":\"click\",\"ref\":1}",
            "{\"action\":\"done\",\"summary\":\"finished the task\"}",
        });
        var gate = new FakeApprovalGate(decision: true);
        var agent = CreateAgent(executor, model, gate, Options_());

        var output = await CollectAsync(agent.RunAsync(Request(), default));

        // Approval was requested once (for the click) and the click executed after it.
        Assert.Equal(1, gate.CallCount);
        Assert.Equal(new[] { ComputerUseActionType.Screenshot, ComputerUseActionType.Click }, executor.Actions);
        Assert.Equal(3, model.CallCount);

        // The HITL marker was emitted, and a STEP marker for the executed click FOLLOWS it.
        var hitlIndex = output.FindIndex(s => s.StartsWith("[HITL_APPROVAL_REQUIRED:", StringComparison.Ordinal));
        Assert.True(hitlIndex >= 0, "expected a HITL_APPROVAL_REQUIRED marker");
        var stepAfterApproval = output.FindIndex(hitlIndex + 1, s => s.StartsWith("[STEP:", StringComparison.Ordinal));
        Assert.True(stepAfterApproval > hitlIndex, "the executed step must follow the approval");

        // The run correlates a session and ends with the done summary.
        Assert.StartsWith("[COMPUTER_USE:", output[0]);
        Assert.Contains(output, s => s.Contains("finished the task"));
    }

    // ── 1b. grounding robustness: self-correct a hallucinated ref, then execute ──

    [Fact]
    public async Task Grounding_HallucinatedRef_SelfCorrects_ThenExecutes()
    {
        // The observation exposes ONLY ref 3. The model first addresses ref 99 (absent →
        // un-groundable); the grounding layer re-asks with the valid-ref list, the model
        // corrects to ref 3 within the GroundingRetries budget, and THAT click executes.
        var executor = new FakeExecutor(WithElements(new InteractiveElement(3, "button", "Search", null)));
        var model = new FakeModel(new[]
        {
            "{\"action\":\"screenshot\"}",         // observe first → the element list (ref 3) becomes known
            "{\"action\":\"click\",\"ref\":99}",   // hallucinated — re-asked, never executed, never gated
            "{\"action\":\"click\",\"ref\":3}",    // corrected — groundable
            "{\"action\":\"done\",\"summary\":\"ok\"}",
        });
        var gate = new FakeApprovalGate(decision: true);
        var agent = CreateAgent(executor, model, gate, Options_(o => o.GroundingRetries = 2));

        var output = await CollectAsync(agent.RunAsync(Request(), default));

        // The screenshot observed the page; the corrected click then ran; the hallucinated one never did.
        Assert.Equal(new[] { ComputerUseActionType.Screenshot, ComputerUseActionType.Click }, executor.Actions);
        Assert.Equal(4, model.CallCount); // screenshot (1); click99 + corrected click3 (2); done (1)
        Assert.Contains(output, s => s.Contains("tự chỉnh lại"));
    }

    // ── 1c. grounding robustness: exhaust retries → fail-closed handoff, nothing runs ──

    [Fact]
    public async Task Grounding_HallucinatedRef_ExhaustsRetries_HandsOff_NeverExecutes()
    {
        // The model keeps addressing a ref that isn't in the observation. After
        // GroundingRetries + 1 attempts the step's fail-closed handoff takes over: the action
        // is never executed and never sent to the approval gate.
        var executor = new FakeExecutor(WithElements(new InteractiveElement(3, "button", "Search", null)));
        var model = new FakeModel(Array.Empty<string>(), fallback: "{\"action\":\"click\",\"ref\":99}");
        var gate = new FakeApprovalGate(decision: true);
        var agent = CreateAgent(executor, model, gate, Options_(o => o.GroundingRetries = 2));

        var output = await CollectAsync(agent.RunAsync(Request(), default));

        Assert.Empty(executor.Actions);   // never executed
        Assert.Equal(0, gate.CallCount);  // never sent to approval
        Assert.Equal(3, model.CallCount); // 1 initial + 2 corrective re-asks, then hand off
        Assert.Contains(output, s => s.Contains("chuyển giao cho con người"));
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
        var executor = new FakeExecutor(WithElements(new InteractiveElement(1, "button", "Continue", null)));
        var model = new FakeModel(new[]
        {
            "{\"action\":\"screenshot\"}",           // observe first so the click (ref 1) is groundable
            "{\"action\":\"click\",\"ref\":1}",
        });
        var gate = new FakeApprovalGate(decision: false); // human rejects
        var agent = CreateAgent(executor, model, gate, Options_());

        var output = await CollectAsync(agent.RunAsync(Request(), default));

        Assert.Equal(1, gate.CallCount);
        Assert.Equal(new[] { ComputerUseActionType.Screenshot }, executor.Actions); // the click never executed
        Assert.DoesNotContain(ComputerUseActionType.Click, executor.Actions);
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

    // ── 8. (GAP 1) coordinate-only type of a secret → refused, NOT executed, NOT gated ──

    [Fact]
    public async Task Loop_CoordinateType_Ungroundable_IsRefused_NotExecuted_NotSentToApproval()
    {
        var executor = new FakeExecutor();
        // A `type` given by x/y with NO ref cannot be grounded to an inspectable element, so
        // the agent cannot rule out that (100,200) is a password/CAPTCHA field.
        // Repeated via fallback so it persists through the grounding self-correction retries —
        // a PERSISTENTLY un-groundable action must still fail closed (never execute, never gate).
        var model = new FakeModel(Array.Empty<string>(), fallback: "{\"action\":\"type\",\"x\":100,\"y\":200,\"text\":\"hunter2\"}");
        var gate = new FakeApprovalGate(decision: true);
        var agent = CreateAgent(executor, model, gate, Options_());

        var output = await CollectAsync(agent.RunAsync(Request(), default));

        Assert.Empty(executor.Actions);                                   // the type never executed
        Assert.Equal(0, gate.CallCount);                                  // never routed to approval
        Assert.DoesNotContain(output, s => s.StartsWith("[HITL_APPROVAL_REQUIRED:", StringComparison.Ordinal));
        Assert.Contains(output, s => s.Contains("🛑"));                   // handed off to a human
    }

    // ── 9. (GAP 1) click whose ref is absent from the current observation → refused ──

    [Fact]
    public async Task Loop_ClickRefAbsentFromCurrentObservation_IsRefused_NotExecuted()
    {
        // The start page exposes only ref 1; the model then clicks ref 99 (stale / never
        // present / dropped by MaxElements) — ungroundable, so fail closed.
        var executor = new FakeExecutor(WithElements(new InteractiveElement(1, "button", "OK", null)));
        // Repeated via fallback so it persists through the grounding retries and still fails closed.
        var model = new FakeModel(Array.Empty<string>(), fallback: "{\"action\":\"click\",\"ref\":99}");
        var gate = new FakeApprovalGate(decision: true);
        var agent = CreateAgent(executor, model, gate, Options_());

        var output = await CollectAsync(agent.RunAsync(Request(startUrl: "https://1.1.1.1/"), default));

        Assert.Equal(new[] { ComputerUseActionType.Navigate }, executor.Actions); // only the start nav ran
        Assert.DoesNotContain(ComputerUseActionType.Click, executor.Actions);
        Assert.Equal(0, gate.CallCount);
        Assert.Contains(output, s => s.Contains("🛑"));
    }

    // ── 10. (GAP 1) a bare key press cannot be grounded → refused (closes the
    //        character-by-character credential-entry bypass around the `type` guard) ──

    [Fact]
    public async Task Loop_BareKeyPress_Ungroundable_IsRefused_NotExecuted()
    {
        var executor = new FakeExecutor(WithElements(new InteractiveElement(1, "textbox", "Search", null)));
        // Repeated via fallback so it persists through the grounding retries and still fails closed.
        var model = new FakeModel(Array.Empty<string>(), fallback: "{\"action\":\"key\",\"keys\":\"h\"}");
        var gate = new FakeApprovalGate(decision: true);
        var agent = CreateAgent(executor, model, gate, Options_());

        var output = await CollectAsync(agent.RunAsync(Request(startUrl: "https://1.1.1.1/"), default));

        Assert.Equal(new[] { ComputerUseActionType.Navigate }, executor.Actions);
        Assert.DoesNotContain(ComputerUseActionType.Key, executor.Actions);
        Assert.Equal(0, gate.CallCount);
        Assert.Contains(output, s => s.Contains("🛑"));
    }

    // ── 11. (GAP 1) a VN-labeled ("Mật khẩu") password field via ref → refused, never typed ──

    [Fact]
    public async Task Loop_VietnamesePasswordField_IsHandedOff_NeverTyped()
    {
        var executor = new FakeExecutor(WithElements(
            new InteractiveElement(1, "textbox", "Tên đăng nhập", null),
            new InteractiveElement(2, "textbox", "Mật khẩu", null)));
        var model = new FakeModel(new[]
        {
            "{\"action\":\"screenshot\"}",
            "{\"action\":\"type\",\"ref\":2,\"text\":\"bimat123\"}",
        });
        var gate = new FakeApprovalGate(decision: true);
        var agent = CreateAgent(executor, model, gate, Options_());

        var output = await CollectAsync(agent.RunAsync(Request(), default));

        Assert.Equal(new[] { ComputerUseActionType.Screenshot }, executor.Actions);
        Assert.DoesNotContain(ComputerUseActionType.Type, executor.Actions);
        Assert.Equal(0, gate.CallCount);
        Assert.Contains(output, s => s.Contains("🛑"));
    }

    // ── 12. (GAP 2) a step whose observation.Url leaves the allowlist → session stops ──

    [Fact]
    public async Task Loop_StopsWhenLandingUrlLeavesAllowlist()
    {
        // The step's observation reports a landing on 8.8.8.8, which is NOT on the allowlist.
        var offsite = new ComputerUseObservation
        {
            Url = "https://8.8.8.8/",
            Title = "Elsewhere",
            Elements = new[] { new InteractiveElement(1, "button", "Go", null) },
        };
        var executor = new FakeExecutor(offsite);
        var model = new FakeModel(new[]
        {
            "{\"action\":\"scroll\",\"direction\":\"down\",\"amount\":3}", // read-only; executes, then lands off-allowlist
            "{\"action\":\"click\",\"ref\":1}",                           // must NEVER run
        });
        var gate = new FakeApprovalGate(decision: true);
        var agent = CreateAgent(executor, model, gate, Options_(o => o.AllowedHosts = new List<string> { "1.1.1.1" }));

        var output = await CollectAsync(agent.RunAsync(Request(), default));

        Assert.Equal(new[] { ComputerUseActionType.Scroll }, executor.Actions); // stopped after the first step
        Assert.DoesNotContain(ComputerUseActionType.Click, executor.Actions);
        Assert.Contains(output, s => s.Contains("🔒"));
    }

    // ── 13. (GAP 2) SSRF gate stops the session even when the landing host is allowlisted ──

    [Fact]
    public async Task Loop_StopsWhenLandingUrlIsSsrfBlocked_EvenIfHostAllowlisted()
    {
        var metadata = new ComputerUseObservation
        {
            Url = "http://169.254.169.254/latest/meta-data",
            Title = "metadata",
        };
        var executor = new FakeExecutor(metadata);
        var model = new FakeModel(new[]
        {
            "{\"action\":\"scroll\",\"direction\":\"down\",\"amount\":1}",
            "{\"action\":\"done\",\"summary\":\"never reached\"}",
        });
        var gate = new FakeApprovalGate(decision: true);
        // Deliberately allowlist the metadata IP to prove the SSRF gate is the backstop.
        var agent = CreateAgent(executor, model, gate, Options_(o => o.AllowedHosts = new List<string> { "169.254.169.254" }));

        var output = await CollectAsync(agent.RunAsync(Request(), default));

        Assert.Equal(new[] { ComputerUseActionType.Scroll }, executor.Actions);
        Assert.Contains(output, s => s.Contains("🔒"));
    }

    // ── 14. (GAP 2) the initial start-URL landing is re-validated too (redirect off-allowlist) ──

    [Fact]
    public async Task Loop_StopsWhenStartUrlRedirectsOffAllowlist_BeforeAskingModel()
    {
        var offsite = new ComputerUseObservation { Url = "https://8.8.8.8/", Title = "redirected" };
        var executor = new FakeExecutor(offsite);
        var model = new FakeModel(new[] { "{\"action\":\"done\",\"summary\":\"never reached\"}" });
        var gate = new FakeApprovalGate(decision: true);
        var agent = CreateAgent(executor, model, gate, Options_(o => o.AllowedHosts = new List<string> { "1.1.1.1" }));

        var output = await CollectAsync(agent.RunAsync(Request(startUrl: "https://1.1.1.1/"), default));

        Assert.Equal(new[] { ComputerUseActionType.Navigate }, executor.Actions); // only the start nav ran
        Assert.Equal(0, model.CallCount);                                          // loop never asked the model
        Assert.Contains(output, s => s.Contains("🔒"));
    }
}
