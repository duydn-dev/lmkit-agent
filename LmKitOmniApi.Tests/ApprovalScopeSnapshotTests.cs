using LmKitOmniApi.Application.Abstractions;
using LmKitOmniApi.Application.Approvals;

namespace LmKitOmniApi.Tests;

/// <summary>
/// <see cref="ApprovalScopeSnapshot"/> on its own — the piece that decides how much
/// authority an approved action gets, so its one invariant is worth stating in isolation
/// rather than only through the orchestrator.
///
/// <para><b>The invariant:</b> whatever comes out of <see cref="ApprovalScopeSnapshot.Narrow"/>
/// permits no action, no document and no web search that either input did not already
/// permit. That is what makes "an approval can never end up with more access than the
/// requesting turn had" true regardless of which side went stale.</para>
/// </summary>
public sealed class ApprovalScopeSnapshotTests
{
    // ── round-tripping ───────────────────────────────────────────────────────

    [Fact]
    public void Capture_ThenRead_PreservesEveryNarrowingField()
    {
        var pinned = Guid.NewGuid();
        var original = new AgentRequestOptions
        {
            AllowWebSearch = false,
            AllowedTools = new[] { "QueryKnowledgeBase", "AnalyzeText" },
            KnowledgeDocumentIds = new[] { pinned }
        };

        Assert.True(ApprovalScopeSnapshot.TryRead(ApprovalScopeSnapshot.Capture(original), out var restored));

        Assert.Equal(original.AllowedTools, restored!.AllowedTools);
        Assert.Equal(original.KnowledgeDocumentIds, restored.KnowledgeDocumentIds);
        Assert.False(restored.AllowWebSearch);
    }

    /// <summary>
    /// The fields that shape a MODEL call are deliberately not persisted: an approved
    /// action is a direct tool invocation with no inference, so replaying them would store
    /// user-authored prompt text for no effect.
    /// </summary>
    [Fact]
    public void Capture_StoresNoPersonaAndNoAdapter()
    {
        var json = ApprovalScopeSnapshot.Capture(new AgentRequestOptions
        {
            AllowedTools = new[] { "AnalyzeText" },
            PersonaPrompt = "Bí mật: chỉ trả lời bằng tiếng Việt.",
            ShowReasoning = true,
            LoraAdapterId = Guid.NewGuid()
        });

        Assert.NotNull(json);
        Assert.DoesNotContain("Bí mật", json!);
        Assert.DoesNotContain("Persona", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Lora", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Reasoning", json, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Nothing restrictive ⇒ no snapshot, so an unscoped turn and a pre-migration row are
    /// indistinguishable — which they must be, because they have to behave identically.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryRead_TreatsAnAbsentSnapshotAsNoScope(string? stored)
    {
        Assert.True(ApprovalScopeSnapshot.TryRead(stored, out var options));
        Assert.Null(options);
    }

    [Fact]
    public void Capture_ReturnsNull_ForOptionsThatNarrowNothing()
    {
        Assert.Null(ApprovalScopeSnapshot.Capture(null));
        Assert.Null(ApprovalScopeSnapshot.Capture(new AgentRequestOptions()));
        Assert.Null(ApprovalScopeSnapshot.Capture(new AgentRequestOptions { ShowReasoning = true }));
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("[1,2,3]")]
    [InlineData("null")]
    public void TryRead_RefusesACorruptSnapshotRatherThanReportingNoScope(string stored)
        => Assert.False(ApprovalScopeSnapshot.TryRead(stored, out _));

    // ── the invariant ────────────────────────────────────────────────────────

    /// <summary>
    /// Randomised over both sides: whatever <see cref="ApprovalScopeSnapshot.Narrow"/>
    /// returns, every action it permits was permitted by BOTH inputs, and so was web
    /// search. The tool vocabulary is the real permission-name set the dispatcher's
    /// whitelist gate compares against.
    /// </summary>
    [Fact]
    public void Narrow_NeverPermitsSomethingEitherSideForbade()
    {
        string[] vocabulary =
        [
            "QueryKnowledgeBase", "AnalyzeText", "RunPython", "RunJavaScript",
            "SearchWeb", "BrowseWeb", "DbWrite", "ReadPdfForm"
        ];
        var rng = new Random(20260907);

        for (var iteration = 0; iteration < 400; iteration++)
        {
            var left = RandomScope(rng, vocabulary);
            var right = RandomScope(rng, vocabulary);

            var result = ApprovalScopeSnapshot.Narrow(left, right);

            foreach (var tool in vocabulary)
            {
                var allowed = Permits(result, tool);
                Assert.False(allowed && !Permits(left, tool),
                    $"widened '{tool}' past the left scope on iteration {iteration}");
                Assert.False(allowed && !Permits(right, tool),
                    $"widened '{tool}' past the right scope on iteration {iteration}");
            }

            var web = result?.AllowWebSearch ?? true;
            Assert.False(web && left is { AllowWebSearch: false });
            Assert.False(web && right is { AllowWebSearch: false });

            // Retrieval REACH rather than the raw field, because the field's empty value
            // means the opposite of the whitelist's: a scope can read document D only if
            // it permits the knowledge-base tool AND its pin either is unrestricted or
            // names D. OutsideThePool stands for every document nobody pinned.
            foreach (var document in DocumentPool.Append(OutsideThePool))
            {
                var reach = CanRead(result, document);
                Assert.False(reach && !CanRead(left, document),
                    $"widened retrieval past the left scope on iteration {iteration}");
                Assert.False(reach && !CanRead(right, document),
                    $"widened retrieval past the right scope on iteration {iteration}");
            }
        }
    }

    /// <summary>Nothing on either side ⇒ nothing narrows, which is the pre-snapshot behaviour.</summary>
    [Fact]
    public void Narrow_OfTwoAbsentScopes_IsStillAbsent()
        => Assert.Null(ApprovalScopeSnapshot.Narrow(null, null));

    /// <summary>One side absent ⇒ the other side stands, unmodified.</summary>
    [Fact]
    public void Narrow_WithOneAbsentSide_KeepsTheOther()
    {
        var scope = new AgentRequestOptions { AllowedTools = new[] { "AnalyzeText" } };
        Assert.Same(scope, ApprovalScopeSnapshot.Narrow(scope, null));
        Assert.Same(scope, ApprovalScopeSnapshot.Narrow(null, scope));
    }

    /// <summary>
    /// Disjoint document pins cannot be expressed as their intersection, because an empty
    /// pin means "everything" downstream. Deny-all is the only answer that is ≤ both.
    /// </summary>
    [Fact]
    public void Narrow_OfDisjointDocumentPins_IsDenyAll()
    {
        var narrowed = ApprovalScopeSnapshot.Narrow(
            new AgentRequestOptions { KnowledgeDocumentIds = new[] { Guid.NewGuid() } },
            new AgentRequestOptions { KnowledgeDocumentIds = new[] { Guid.NewGuid() } });

        Assert.NotNull(narrowed?.AllowedTools);
        Assert.Empty(narrowed!.AllowedTools!);
        Assert.False(narrowed.AllowWebSearch);
    }

    /// <summary>
    /// An EMPTY pin on one side is an input that never restricted anything, not an empty
    /// intersection — mistaking the two would refuse an execution that was never
    /// document-scoped.
    /// </summary>
    [Fact]
    public void Narrow_TreatsAnEmptyDocumentPinAsNoRestriction()
    {
        var pinned = Guid.NewGuid();

        var narrowed = ApprovalScopeSnapshot.Narrow(
            new AgentRequestOptions { KnowledgeDocumentIds = Array.Empty<Guid>() },
            new AgentRequestOptions { KnowledgeDocumentIds = new[] { pinned } });

        Assert.Equal(new[] { pinned }, narrowed!.KnowledgeDocumentIds!.ToArray());
    }

    /// <summary>Tool names are matched case-insensitively, exactly like the dispatcher's gate.</summary>
    [Fact]
    public void Narrow_MatchesToolNamesCaseInsensitively()
    {
        var narrowed = ApprovalScopeSnapshot.Narrow(
            new AgentRequestOptions { AllowedTools = new[] { "queryknowledgebase", "AnalyzeText" } },
            new AgentRequestOptions { AllowedTools = new[] { "QueryKnowledgeBase" } });

        Assert.Single(narrowed!.AllowedTools!);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    /// <summary>The dispatcher's reading: a null whitelist permits everything, otherwise membership decides.</summary>
    private static bool Permits(AgentRequestOptions? scope, string tool)
        => scope?.AllowedTools is not { } whitelist
           || whitelist.Contains(tool, StringComparer.OrdinalIgnoreCase);

    /// <summary>RagPipelineService's reading: only a non-empty pin restricts anything.</summary>
    private static bool Restricts(IReadOnlyCollection<Guid>? pin) => pin is { Count: > 0 };

    /// <summary>
    /// Whether a scope can actually retrieve one document — the composition of the tool
    /// whitelist and the document pin, which is what the product ends up enforcing.
    /// </summary>
    private static bool CanRead(AgentRequestOptions? scope, Guid document)
        => Permits(scope, "QueryKnowledgeBase")
           && (!Restricts(scope?.KnowledgeDocumentIds) || scope!.KnowledgeDocumentIds!.Contains(document));

    private static AgentRequestOptions? RandomScope(Random rng, string[] vocabulary)
    {
        if (rng.Next(5) == 0) return null;

        return new AgentRequestOptions
        {
            AllowWebSearch = rng.Next(2) == 0,
            AllowedTools = rng.Next(4) == 0
                ? null
                : vocabulary.Where(_ => rng.Next(2) == 0).ToArray(),
            // A small fixed pool so intersections are sometimes non-empty and sometimes not.
            KnowledgeDocumentIds = rng.Next(4) == 0
                ? null
                : DocumentPool.Where(_ => rng.Next(2) == 0).ToArray()
        };
    }

    private static readonly Guid[] DocumentPool =
    [
        Guid.Parse("11111111-1111-1111-1111-111111111111"),
        Guid.Parse("22222222-2222-2222-2222-222222222222"),
        Guid.Parse("33333333-3333-3333-3333-333333333333")
    ];

    /// <summary>Stands for every document nobody ever pinned.</summary>
    private static readonly Guid OutsideThePool = Guid.Parse("99999999-9999-9999-9999-999999999999");
}
