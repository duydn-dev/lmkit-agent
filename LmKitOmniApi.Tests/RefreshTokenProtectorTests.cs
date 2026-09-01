using LmKitOmniApi.Infrastructure.Security;

namespace LmKitOmniApi.Tests;

public class RefreshTokenProtectorTests
{
    [Fact]
    public void Generate_ReturnsUniqueHighEntropyTokens()
    {
        var first = RefreshTokenProtector.Generate();
        var second = RefreshTokenProtector.Generate();

        Assert.NotEqual(first, second);
        Assert.Equal(32, Convert.FromBase64String(first).Length);
    }

    [Fact]
    public void Hash_IsDeterministicAndDoesNotStoreRawToken()
    {
        const string token = "sample-refresh-token";

        var first = RefreshTokenProtector.Hash(token);
        var second = RefreshTokenProtector.Hash(token);

        Assert.Equal(first, second);
        Assert.NotEqual(token, first);
        Assert.Equal(64, first.Length);
    }
}
