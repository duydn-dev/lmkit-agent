using LmKitOmniApi.Infrastructure.AI.Voice;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LmKitOmniApi.Tests;

/// <summary>
/// How the hosted service CHOOSES its mode, and every way it stands down.
///
/// Priority one for the dispatcher work was not breaking the shipped single-room agent, so the
/// first two tests are the ones that matter: with <c>Voice:DispatcherEnabled</c> false the
/// service takes exactly the path it always has, and <c>Voice:LiveAgentEnabled</c> remains the
/// master switch that turns everything off.
///
/// The rest pin the stand-downs. A dispatcher that cannot run correctly must log and stop — it
/// must never crash the host, and it must never quietly fall back to serving one hard-coded
/// user's room while an operator believes it is serving everybody.
/// </summary>
public sealed class VoiceRoomAgentHostedServiceTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Disabled_IsAStrictNoOp_EvenWithTheDispatcherSwitchOn()
    {
        // LiveAgentEnabled is the master switch: nothing runs while it is false.
        await using var host = Build(options =>
        {
            options.LiveAgentEnabled = false;
            options.DispatcherEnabled = true;
        });

        await host.Service.StartAsync(CancellationToken.None);

        await AssertStoodDownAsync(host.Service);
        await host.Service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task WithoutCredentials_StandsDown_InEitherMode()
    {
        foreach (var dispatcher in new[] { false, true })
        {
            await using var host = Build(options =>
            {
                options.LiveAgentEnabled = true;
                options.DispatcherEnabled = dispatcher;
            });

            await host.Service.StartAsync(CancellationToken.None);

            await AssertStoodDownAsync(host.Service);
            await host.Service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task SingleRoomMode_WithoutAnAgentIdentity_StandsDown_ExactlyAsItAlwaysHas()
    {
        // The shipped behaviour: rooms are per user, so with no Voice:AgentTenantId/AgentUserId
        // there is no room worth joining and the service says so instead of joining one nobody
        // is in. Unchanged by the dispatcher work.
        await using var host = Build(options =>
        {
            options.LiveAgentEnabled = true;
            options.DispatcherEnabled = false;
            options.LiveKitUrl = "ws://livekit.invalid:7880";
            options.LiveKitApiKey = "k";
            options.LiveKitApiSecret = "s";
        });

        await host.Service.StartAsync(CancellationToken.None);

        await AssertStoodDownAsync(host.Service);
        await host.Service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task DispatcherMode_WithoutTheConsentRegistry_StandsDown_RatherThanServingNobodySilently()
    {
        // If the Program.cs registration is never applied, no user can opt in — so the honest
        // outcome is a loud stand-down, not a dispatcher looping over an empty ledger for ever.
        await using var host = Build(
            options =>
            {
                options.LiveAgentEnabled = true;
                options.DispatcherEnabled = true;
                options.LiveKitUrl = "ws://livekit.invalid:7880";
                options.LiveKitApiKey = "k";
                options.LiveKitApiSecret = "s";
            },
            registry: null);

        await host.Service.StartAsync(CancellationToken.None);

        await AssertStoodDownAsync(host.Service);
        await host.Service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task DispatcherMode_WithANonsenseCap_StandsDown_RatherThanGuessing()
    {
        await using var host = Build(
            options =>
            {
                options.LiveAgentEnabled = true;
                options.DispatcherEnabled = true;
                options.LiveKitUrl = "ws://livekit.invalid:7880";
                options.LiveKitApiKey = "k";
                options.LiveKitApiSecret = "s";
                options.MaxConcurrentRooms = 0;      // invalid
            },
            registry: new EmptyRegistry());

        await host.Service.StartAsync(CancellationToken.None);

        await AssertStoodDownAsync(host.Service);
        await host.Service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task DispatcherMode_Runs_AndStopsPromptlyOnShutdown()
    {
        var registry = new EmptyRegistry();
        await using var host = Build(
            options =>
            {
                options.LiveAgentEnabled = true;
                options.DispatcherEnabled = true;
                options.LiveKitUrl = "ws://livekit.invalid:7880";
                options.LiveKitApiKey = "k";
                options.LiveKitApiSecret = "s";
                options.DispatcherPollSeconds = 1;
            },
            registry: registry);

        await host.Service.StartAsync(CancellationToken.None);

        // It is genuinely running: the loop reads the consent ledger. No LiveKit code is
        // reachable because the ledger is empty, so no room is ever joined.
        var deadline = DateTime.UtcNow + Patience;
        while (registry.Reads == 0 && DateTime.UtcNow < deadline) await Task.Delay(10);
        Assert.True(registry.Reads > 0, "the dispatcher never read the consent ledger");
        Assert.False(host.Service.ExecuteTask!.IsCompleted);

        var stop = host.Service.StopAsync(CancellationToken.None);
        var finished = await Task.WhenAny(stop, Task.Delay(Patience));
        Assert.Same(stop, finished);
        await stop;
        Assert.True(host.Service.ExecuteTask!.IsCompleted);
    }

    // ── helpers ──

    /// <summary>
    /// The service reached a stand-down: its background task finished, on its own, without
    /// faulting. Awaited rather than inspected synchronously because the host may start
    /// ExecuteAsync off the calling thread.
    /// </summary>
    private static async Task AssertStoodDownAsync(VoiceRoomAgentHostedService service)
    {
        var execute = service.ExecuteTask;
        Assert.NotNull(execute);
        var finished = await Task.WhenAny(execute!, Task.Delay(Patience));
        Assert.Same(execute, finished);
        await execute!;                                  // surfaces a fault as a test failure
        Assert.True(execute.IsCompletedSuccessfully);
    }

    private static TestHost Build(Action<VoiceOptions> configure, IVoiceAgentConsentRegistry? registry = null)
    {
        var options = new VoiceOptions();
        configure(options);

        var services = new ServiceCollection();
        services.AddLogging();
        if (registry is not null) services.AddSingleton(registry);
        var provider = services.BuildServiceProvider();

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>()).Build();

        var service = new VoiceRoomAgentHostedService(
            Options.Create(options),
            configuration,
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<VoiceRoomAgentHostedService>.Instance);

        return new TestHost(service, provider);
    }

    private sealed class TestHost : IAsyncDisposable
    {
        public TestHost(VoiceRoomAgentHostedService service, ServiceProvider provider)
        {
            Service = service;
            Provider = provider;
        }

        public VoiceRoomAgentHostedService Service { get; }
        public ServiceProvider Provider { get; }

        public async ValueTask DisposeAsync()
        {
            Service.Dispose();
            await Provider.DisposeAsync();
        }
    }

    /// <summary>A consent ledger nobody has opted into; counts how often it was read.</summary>
    private sealed class EmptyRegistry : IVoiceAgentConsentRegistry
    {
        private int _reads;
        public int Reads => Volatile.Read(ref _reads);
        public int Count => 0;

        public IReadOnlyList<VoiceAgentGrant> ActiveGrants(DateTimeOffset nowUtc)
        {
            Interlocked.Increment(ref _reads);
            return Array.Empty<VoiceAgentGrant>();
        }

        public bool Revoke(Guid tenantId, Guid userId, string? roomLabel) => false;

        public bool TryGrant(
            Guid tenantId, Guid userId, string? roomLabel, string? voice, DateTimeOffset nowUtc,
            out VoiceAgentGrant? grant, out string? error)
        {
            grant = null;
            error = "not supported in this fake";
            return false;
        }
    }
}
