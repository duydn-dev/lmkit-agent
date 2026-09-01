using LmKitOmniApi.Services;
using Microsoft.Extensions.Configuration;

namespace LmKitOmniApi.Tests;

public class LmModelManagerConcurrencyTests
{
    [Fact]
    public async Task ChatInferenceLease_IsBoundedAndReleased()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SemaphoreLimits:Chat"] = "1"
            })
            .Build();
        using var manager = new LmModelManager(configuration);

        var first = await manager.AcquireChatInferenceAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await manager.AcquireChatInferenceAsync(timeout.Token));

        await first.DisposeAsync();
        var second = await manager.AcquireChatInferenceAsync();
        await second.DisposeAsync();
    }
}
