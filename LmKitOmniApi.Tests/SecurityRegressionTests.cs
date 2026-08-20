using System.Net;
using System.Text;
using LmKitOmniApi.Infrastructure.Security;
using LmKitOmniApi.Infrastructure.AI.Resilience;
using LmKitOmniApi.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LmKitOmniApi.Tests;

public sealed class SecurityRegressionTests
{
    [Theory]
    [InlineData("huggingface.co", true)]
    [InlineData("cdn-lfs.huggingface.co", true)]
    [InlineData("cas-bridge.xethub.hf.co", true)]
    [InlineData("huggingface.co.attacker.example", false)]
    [InlineData("localhost", false)]
    [InlineData("127.0.0.1", false)]
    public void ModelHostAllowlist_RequiresExactTrustedSuffix(string host, bool expected) =>
        Assert.Equal(expected, LmModelManager.IsTrustedModelHost(host));

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("10.10.0.1", true)]
    [InlineData("172.16.0.1", true)]
    [InlineData("192.168.1.1", true)]
    [InlineData("169.254.1.1", true)]
    [InlineData("8.8.8.8", false)]
    public void ModelAddressGuard_BlocksPrivateNetworks(string address, bool expected) =>
        Assert.Equal(expected, LmModelManager.IsPrivateOrLocalAddress(IPAddress.Parse(address)));

    [Fact]
    public async Task UploadSignatureValidator_AcceptsRealPdfHeader()
    {
        await using var stream = new MemoryStream(Encoding.ASCII.GetBytes("%PDF-1.7\ncontent"));
        var file = new FormFile(stream, 0, stream.Length, "file", "report.pdf");
        Assert.True(await UploadFileValidator.HasExpectedSignatureAsync(file, ".pdf", CancellationToken.None));
    }

    [Fact]
    public async Task UploadSignatureValidator_RejectsRenamedExecutable()
    {
        await using var stream = new MemoryStream(new byte[] { 0x4D, 0x5A, 0x90, 0x00 });
        var file = new FormFile(stream, 0, stream.Length, "file", "malware.pdf");
        Assert.False(await UploadFileValidator.HasExpectedSignatureAsync(file, ".pdf", CancellationToken.None));
    }

    [Fact]
    public void McpHeaderProtector_EncryptsAndRoundTrips()
    {
        var provider = new EphemeralDataProtectionProvider();
        var protector = new McpHeaderProtector(provider);
        const string secret = "{\"Authorization\":\"Bearer secret-token\"}";

        var encrypted = protector.Protect(secret);

        Assert.StartsWith("dp:v1:", encrypted);
        Assert.DoesNotContain("secret-token", encrypted);
        Assert.Equal(secret, protector.Unprotect(encrypted));
    }

    [Fact]
    public async Task ResiliencePolicy_DoesNotRetryUnsafeAction()
    {
        var cache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        var policy = new AgentResiliencePolicy(NullLogger<AgentResiliencePolicy>.Instance, cache);
        var attempts = 0;

        var result = await policy.ExecuteWithResilienceAsync(
            "MCP_WRITE_TEST",
            _ =>
            {
                attempts++;
                throw new InvalidOperationException("side effect may already have happened");
            },
            "fallback",
            retrySafe: false);

        Assert.Equal("fallback", result);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task RequiredResiliencePolicy_DoesNotRetryUnsafeAction()
    {
        var cache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        var policy = new AgentResiliencePolicy(NullLogger<AgentResiliencePolicy>.Instance, cache);
        var attempts = 0;

        await Assert.ThrowsAsync<InvalidOperationException>(() => policy.ExecuteRequiredWithResilienceAsync<string>(
            "APPROVED_WRITE_TEST",
            _ =>
            {
                attempts++;
                throw new InvalidOperationException("write failed ambiguously");
            },
            retrySafe: false));

        Assert.Equal(1, attempts);
    }
}
