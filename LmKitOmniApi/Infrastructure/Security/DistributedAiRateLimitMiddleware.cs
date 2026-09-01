using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.RateLimiting;
using StackExchange.Redis;

namespace LmKitOmniApi.Infrastructure.Security;

public sealed class DistributedAiRateLimitMiddleware
{
    private const string IncrementScript = """
        local current = redis.call('INCR', KEYS[1])
        if current == 1 then
            redis.call('EXPIRE', KEYS[1], ARGV[1])
        end
        local ttl = redis.call('TTL', KEYS[1])
        return { current, ttl }
        """;

    private readonly RequestDelegate _next;
    private readonly ILogger<DistributedAiRateLimitMiddleware> _logger;
    private readonly int _requestLimit;
    private readonly int _windowSeconds;

    public DistributedAiRateLimitMiddleware(
        RequestDelegate next,
        ILogger<DistributedAiRateLimitMiddleware> logger,
        IConfiguration configuration)
    {
        _next = next;
        _logger = logger;
        _requestLimit = configuration.GetValue("RateLimiting:AiRequestsPerWindow", 10);
        _windowSeconds = configuration.GetValue("RateLimiting:AiWindowSeconds", 60);
        if (_requestLimit <= 0 || _windowSeconds <= 0)
            throw new InvalidOperationException("Distributed AI rate-limit values must be greater than zero.");
    }

    public async Task InvokeAsync(HttpContext context, IServiceProvider services)
    {
        var policy = context.GetEndpoint()?.Metadata.GetMetadata<EnableRateLimitingAttribute>();
        if (!string.Equals(policy?.PolicyName, "ai-agent", StringComparison.Ordinal))
        {
            await _next(context);
            return;
        }

        var multiplexer = services.GetService<IConnectionMultiplexer>();
        if (multiplexer is null)
        {
            await _next(context);
            return;
        }

        var partition = context.User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? context.Connection.RemoteIpAddress?.ToString()
            ?? "anonymous";
        var window = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / _windowSeconds;
        var key = $"rate:ai:{BuildPartitionHash(partition)}:{window}";

        try
        {
            context.RequestAborted.ThrowIfCancellationRequested();
            var result = (RedisResult[]?)await multiplexer.GetDatabase().ScriptEvaluateAsync(
                IncrementScript,
                new RedisKey[] { key },
                new RedisValue[] { _windowSeconds });
            var count = result is { Length: >= 2 } ? (long)result[0] : 0;
            var ttl = result is { Length: >= 2 } ? Math.Max(1, (long)result[1]) : _windowSeconds;

            context.Response.Headers["X-RateLimit-Limit"] = _requestLimit.ToString();
            context.Response.Headers["X-RateLimit-Remaining"] = Math.Max(0, _requestLimit - count).ToString();
            if (count > _requestLimit)
            {
                context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                context.Response.Headers.RetryAfter = ttl.ToString();
                await context.Response.WriteAsJsonAsync(new
                {
                    title = "Too many AI requests.",
                    status = StatusCodes.Status429TooManyRequests
                }, context.RequestAborted);
                return;
            }
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch (RedisException ex)
        {
            // The in-process ASP.NET limiter remains active as a safe fallback.
            _logger.LogWarning(ex, "Distributed AI rate limiter is unavailable; using local limiter only.");
        }

        await _next(context);
    }

    internal static string BuildPartitionHash(string partition)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(partition));
        return Convert.ToHexString(hash.AsSpan(0, 12)).ToLowerInvariant();
    }
}
