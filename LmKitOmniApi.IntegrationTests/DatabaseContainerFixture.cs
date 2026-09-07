using DotNet.Testcontainers.Containers;

namespace LmKitOmniApi.IntegrationTests;

/// <summary>
/// Base xUnit class-fixture that spins up ONE real engine container for a test class.
///
/// <para><b>Docker-optional, but only when nobody said otherwise.</b> On a developer laptop
/// with no Docker daemon the container cannot start, the reason is captured into
/// <see cref="SkipReason"/>, and every dependent <c>[SkippableFact]</c> SKIPS — the point of
/// these opt-in tests being safe to run anywhere.</para>
///
/// <para><b>Why that used to be a lie.</b> The same catch swallowed EVERY failure: a broken
/// image tag, an engine that refused the seed SQL, a bug in the fixture itself. The project
/// reported "success" while proving nothing, and because it was also absent from CI there was
/// no environment in which that could be noticed. Setting
/// <see cref="RequireContainersVariable"/> reverses the default: a container that will not
/// start is then a hard failure with the original exception attached, so an environment that
/// is SUPPOSED to have Docker can never quietly downgrade itself to a green no-op. CI sets
/// it; see <c>.github/workflows/ci.yml</c>, job <c>integration</c>.</para>
/// </summary>
public abstract class DatabaseContainerFixture : IAsyncLifetime
{
    /// <summary>
    /// When set to anything other than <c>0</c>/<c>false</c>/empty, a container that cannot
    /// start fails the fixture instead of skipping its tests.
    /// </summary>
    public const string RequireContainersVariable = "LMKIT_REQUIRE_CONTAINERS";

    private IContainer? _container;

    /// <summary>
    /// True when this environment has declared that containers MUST work, so a startup
    /// failure is a real failure rather than an absent dependency.
    /// </summary>
    public static bool ContainersAreRequired => IsTruthy(
        Environment.GetEnvironmentVariable(RequireContainersVariable));

    internal static bool IsTruthy(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && !value.Equals("0", StringComparison.Ordinal)
        && !value.Equals("false", StringComparison.OrdinalIgnoreCase)
        && !value.Equals("no", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The seam <see cref="ContainerRequirementTests"/> uses to exercise BOTH branches of the
    /// catch below without a Docker daemon to break. Real fixtures never override it.
    /// </summary>
    protected virtual bool RequireContainers => ContainersAreRequired;

    /// <summary>Non-null when the container could not be started/seeded → tests skip.</summary>
    public string? SkipReason { get; private set; }

    /// <summary>Live connection string once the container is up; empty when skipped.</summary>
    public string ConnectionString { get; private set; } = string.Empty;

    protected abstract IContainer Build();

    protected virtual string ResolveConnectionString(IContainer container) =>
        ((IDatabaseContainer)container).GetConnectionString();

    protected abstract Task SeedAsync(string connectionString, CancellationToken ct);

    public async Task InitializeAsync()
    {
        try
        {
            _container = Build();
            await _container.StartAsync();
            ConnectionString = ResolveConnectionString(_container);
            await SeedAsync(ConnectionString, CancellationToken.None);
        }
        catch (Exception ex)
        {
            await SafeDisposeAsync();

            if (RequireContainers)
            {
                // Loud on purpose: this environment promised Docker, so a container that will
                // not start is a broken build, not a missing optional dependency. The original
                // exception is kept as the inner exception so the reason survives.
                throw new InvalidOperationException(
                    $"{GetType().Name} could not start its container, and {RequireContainersVariable} "
                    + "is set, so this is a failure rather than a skip. Either the Docker daemon, the "
                    + "image, or the seed is broken. Unset "
                    + $"{RequireContainersVariable} only in environments where Docker is genuinely "
                    + "optional. Original failure: "
                    + $"{ex.GetType().Name}: {ex.Message}",
                    ex);
            }

            // Docker genuinely optional here → record why and let the [SkippableFact]s skip.
            SkipReason = $"Live engine unavailable (Docker not running or image unreachable) — test skipped. "
                + $"Set {RequireContainersVariable}=1 to make this a failure instead. "
                + $"{ex.GetType().Name}: {Truncate(ex.Message)}";
        }
    }

    public Task DisposeAsync() => SafeDisposeAsync();

    private async Task SafeDisposeAsync()
    {
        if (_container is null) return;
        try { await _container.DisposeAsync(); } catch { /* best effort cleanup */ }
        _container = null;
    }

    private static string Truncate(string value) => value.Length <= 240 ? value : value[..240];
}

/// <summary>
/// Guards the guard. A skip gate is only worth having if the environment that is meant to
/// disarm it actually does, and the failure mode of getting that wrong is invisible: CI sets
/// the variable, nothing reads it the way CI spells it, every test skips, the job is green.
/// </summary>
public sealed class ContainerRequirementTests
{
    [Fact]
    public void RequestingRequiredContainers_ActuallyArmsTheGate()
    {
        var raw = Environment.GetEnvironmentVariable(DatabaseContainerFixture.RequireContainersVariable);
        if (string.IsNullOrWhiteSpace(raw)) return; // Docker is optional in this environment.

        Assert.True(
            DatabaseContainerFixture.ContainersAreRequired,
            $"{DatabaseContainerFixture.RequireContainersVariable} is set to '{raw}', but the fixture "
            + "did not read that as 'containers are required', so every container failure would "
            + "still SKIP and this job could pass without starting a single engine.");
    }

    /// <summary>
    /// A fixture whose container can never start, so both branches of the catch can be
    /// exercised without a Docker daemon to take down.
    /// </summary>
    private sealed class UnstartableFixture(bool required) : DatabaseContainerFixture
    {
        public const string Failure = "simulated container startup failure";

        protected override bool RequireContainers => required;

        protected override IContainer Build() => throw new InvalidOperationException(Failure);

        protected override Task SeedAsync(string connectionString, CancellationToken ct) =>
            Task.CompletedTask;
    }

    [Fact]
    public async Task AContainerThatCannotStart_FailsLoudlyWhenContainersAreRequired()
    {
        var fixture = new UnstartableFixture(required: true);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(fixture.InitializeAsync);

        Assert.Contains(DatabaseContainerFixture.RequireContainersVariable, error.Message, StringComparison.Ordinal);
        Assert.Contains(UnstartableFixture.Failure, error.Message, StringComparison.Ordinal);
        Assert.Null(fixture.SkipReason);
    }

    [Fact]
    public async Task AContainerThatCannotStart_SkipsWhenContainersAreOptional()
    {
        var fixture = new UnstartableFixture(required: false);

        await fixture.InitializeAsync();

        Assert.NotNull(fixture.SkipReason);
        Assert.Contains(UnstartableFixture.Failure, fixture.SkipReason, StringComparison.Ordinal);
        // The skip has to say how to stop it being a skip, or nobody ever will.
        Assert.Contains(DatabaseContainerFixture.RequireContainersVariable, fixture.SkipReason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    [InlineData("yes", true)]
    [InlineData("0", false)]
    [InlineData("false", false)]
    [InlineData("no", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void TheRequirementFlag_ParsesTheSpellingsAnEnvironmentIsLikelyToUse(string? value, bool expected) =>
        Assert.Equal(expected, DatabaseContainerFixture.IsTruthy(value));
}
