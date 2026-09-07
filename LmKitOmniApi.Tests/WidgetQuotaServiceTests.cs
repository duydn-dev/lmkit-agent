using LmKitOmniApi.Application.Widget;
using Microsoft.Extensions.Logging.Abstractions;

namespace LmKitOmniApi.Tests;

/// <summary>
/// The widget quota is the only budget standing between an anonymous embedder and
/// unbounded model inference, so counting must be ATOMIC. The previous
/// get → parse → +1 → set over IDistributedCache let concurrent turns read the
/// same value and write back the same increment, which made the configured limit
/// trivially exceedable.
/// <para>
/// Every test uses a fresh tenant id, so its counter keys are untouched by the
/// rest of the suite, and pins the DAY budget as well as the minute one — a run
/// that straddles a minute boundary must still be bounded.
/// </para>
/// </summary>
public sealed class WidgetQuotaServiceTests
{
    private static WidgetQuotaService CreateService()
        => new(NullLogger<WidgetQuotaService>.Instance);

    [Fact]
    public async Task TryConsume_GrantsExactlyTheConfiguredBudget()
    {
        var quota = CreateService();
        var tenantId = Guid.NewGuid();

        var granted = 0;
        for (var attempt = 0; attempt < 10; attempt++)
        {
            if (await quota.TryConsumeAsync(tenantId, "https://shop.example.com", 4, 4, CancellationToken.None))
                granted++;
        }

        Assert.Equal(4, granted);
    }

    /// <summary>
    /// The SAFETY property, which is the one this test is named for: 200 racing callers can
    /// never be granted more than the budget.
    ///
    /// <para>It deliberately does NOT assert equality. <c>TryConsumeAsync</c> increments the
    /// minute and day counters in two SEPARATE atomic operations and grants only when both
    /// land within budget. Each counter therefore admits exactly <c>budget</c> callers — but
    /// not necessarily the SAME ones, because another caller's day increment can land between
    /// this caller's two. Grants are the intersection of those two sets, so under real
    /// contention the count can come in one or two under. Asserting equality made this test
    /// fail roughly one run in ten on a loaded machine, and it was asserting a liveness
    /// property the implementation never promised rather than the safety property it names.</para>
    ///
    /// <para>Under-granting is the fail-closed direction and is left as-is: a caller sees one
    /// spurious rejection under heavy contention. <see cref="TryConsume_GrantsExactlyTheConfiguredBudget"/>
    /// proves the budget is fully usable in the sequential case, which is every real caller.
    /// Making it exact under contention means one atomic operation spanning both counters.</para>
    /// </summary>
    [Fact]
    public async Task TryConsume_UnderConcurrency_NeverExceedsTheBudget()
    {
        var quota = CreateService();
        var tenantId = Guid.NewGuid();
        const int budget = 10;

        var results = await Task.WhenAll(Enumerable.Range(0, 200).Select(_ =>
            Task.Run(() => quota.TryConsumeAsync(
                tenantId, "https://shop.example.com", budget, budget, CancellationToken.None))));

        var granted = results.Count(g => g);

        Assert.True(granted <= budget, $"granted {granted} of a {budget} budget — the cap leaked");
        Assert.True(granted > 0, "every one of 200 callers was rejected on a fresh budget");
    }

    [Fact]
    public async Task TryConsume_KeepsTenantsAndOriginsIndependent()
    {
        var quota = CreateService();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        Assert.True(await quota.TryConsumeAsync(first, "https://a.example.com", 1, 1, CancellationToken.None));
        Assert.False(await quota.TryConsumeAsync(first, "https://a.example.com", 1, 1, CancellationToken.None));

        // A different origin of the same tenant, and a different tenant entirely,
        // each get their own bucket.
        Assert.True(await quota.TryConsumeAsync(first, "https://b.example.com", 1, 1, CancellationToken.None));
        Assert.True(await quota.TryConsumeAsync(second, "https://a.example.com", 1, 1, CancellationToken.None));
    }

    [Fact]
    public async Task TryConsume_EnforcesTheDailyBudgetIndependentlyOfTheMinuteOne()
    {
        var quota = CreateService();
        var tenantId = Guid.NewGuid();

        Assert.True(await quota.TryConsumeAsync(tenantId, "https://shop.example.com", 100, 2, CancellationToken.None));
        Assert.True(await quota.TryConsumeAsync(tenantId, "https://shop.example.com", 100, 2, CancellationToken.None));
        Assert.False(await quota.TryConsumeAsync(tenantId, "https://shop.example.com", 100, 2, CancellationToken.None));
    }
}
