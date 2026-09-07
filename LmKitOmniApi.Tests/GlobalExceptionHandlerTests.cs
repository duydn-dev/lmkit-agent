using System.Text.Json;
using LmKitOmniApi.Infrastructure.Exceptions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;

namespace LmKitOmniApi.Tests;

/// <summary>
/// Status mapping and streaming safety for the last-chance exception handler.
///
/// The defects covered here:
///  - every <see cref="InvalidOperationException"/> was reported as 400 Bad Request, even
///    though this codebase throws that type for server misconfiguration (unconfigured SearXNG
///    base URL, invalid AiModels limits, an unloadable widget signing key) and for every EF
///    Single()/First() invariant violation — so genuine outages never showed up as 5xx;
///  - <c>WriteAsJsonAsync</c> ran with no <see cref="HttpResponse.HasStarted"/> check, which
///    throws on a streaming/SSE response whose headers are already on the wire;
///  - a client hanging up mid-stream was logged at Error.
/// </summary>
public sealed class GlobalExceptionHandlerTests
{
    [Theory]
    // Server-side misconfiguration / broken invariants — the exact shapes this codebase throws.
    [InlineData(typeof(InvalidOperationException), StatusCodes.Status500InternalServerError)]
    [InlineData(typeof(NullReferenceException), StatusCodes.Status500InternalServerError)]
    [InlineData(typeof(TimeoutException), StatusCodes.Status500InternalServerError)]
    // Caller-shaped failures stay in 4xx.
    [InlineData(typeof(ArgumentException), StatusCodes.Status400BadRequest)]
    [InlineData(typeof(ArgumentNullException), StatusCodes.Status400BadRequest)]
    [InlineData(typeof(ArgumentOutOfRangeException), StatusCodes.Status400BadRequest)]
    [InlineData(typeof(UnauthorizedAccessException), StatusCodes.Status401Unauthorized)]
    public void Classify_SeparatesServerFaultsFromCallerMistakes(Type exceptionType, int expectedStatus)
    {
        var exception = (Exception)Activator.CreateInstance(exceptionType)!;

        Assert.Equal(expectedStatus, GlobalExceptionHandler.Classify(exception).Status);
    }

    [Fact]
    public async Task MisconfigurationFault_IsWrittenAs500AndLoggedAtError()
    {
        var logger = new RecordingLogger<GlobalExceptionHandler>();
        var handler = new GlobalExceptionHandler(logger);
        var context = CreateContext(out var body);

        var handled = await handler.TryHandleAsync(
            context,
            // Verbatim shape of SearxSearchProvider's misconfiguration guard.
            new InvalidOperationException("SearXNG base URL is not configured (WebSearch:Searx:BaseUrl)."),
            CancellationToken.None);

        Assert.True(handled);
        Assert.Equal(StatusCodes.Status500InternalServerError, context.Response.StatusCode);
        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Error);

        var problem = JsonSerializer.Deserialize<JsonElement>(ReadBody(body));
        Assert.Equal(StatusCodes.Status500InternalServerError, problem.GetProperty("status").GetInt32());
        // The message names a config key: it is logged, never returned to the caller.
        Assert.DoesNotContain("WebSearch:Searx", ReadBody(body), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallerMistake_IsStillWrittenAs400()
    {
        var handler = new GlobalExceptionHandler(new RecordingLogger<GlobalExceptionHandler>());
        var context = CreateContext(out var body);

        await handler.TryHandleAsync(context, new ArgumentException("bad input"), CancellationToken.None);

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        var problem = JsonSerializer.Deserialize<JsonElement>(ReadBody(body));
        Assert.Equal("Bad Request", problem.GetProperty("title").GetString());
    }

    /// <summary>
    /// An SSE / chunked response has already flushed headers when a mid-stream fault
    /// surfaces. Touching StatusCode or writing a body then throws from inside the handler,
    /// turning a stream fault into a crash.
    /// </summary>
    [Fact]
    public async Task StartedResponse_IsLeftAloneInsteadOfThrowing()
    {
        var logger = new RecordingLogger<GlobalExceptionHandler>();
        var handler = new GlobalExceptionHandler(logger);
        var context = CreateStartedResponseContext();

        var handled = await handler.TryHandleAsync(
            context,
            new InvalidOperationException("mid-stream failure"),
            CancellationToken.None);

        Assert.True(handled);
        // Untouched: writing to a started response is what used to blow up.
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Contains(logger.Entries, entry => entry.Message.Contains("already started", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ClientDisconnect_IsNotAnError()
    {
        var logger = new RecordingLogger<GlobalExceptionHandler>();
        var handler = new GlobalExceptionHandler(logger);
        var context = CreateContext(out var body);
        using var aborted = new CancellationTokenSource();
        await aborted.CancelAsync();
        context.RequestAborted = aborted.Token;

        var handled = await handler.TryHandleAsync(
            context,
            new OperationCanceledException(aborted.Token),
            aborted.Token);

        Assert.True(handled);
        Assert.DoesNotContain(logger.Entries, entry => entry.Level >= LogLevel.Error);
        Assert.Empty(ReadBody(body));
    }

    /// <summary>
    /// A cancellation the CLIENT did not cause (a server-side budget expiring) is still a
    /// real fault and must not be laundered into a silent success.
    /// </summary>
    [Fact]
    public async Task ServerSideCancellation_IsStillReportedAsAFault()
    {
        var handler = new GlobalExceptionHandler(new RecordingLogger<GlobalExceptionHandler>());
        var context = CreateContext(out _);

        await handler.TryHandleAsync(context, new OperationCanceledException(), CancellationToken.None);

        Assert.Equal(StatusCodes.Status500InternalServerError, context.Response.StatusCode);
    }

    private static DefaultHttpContext CreateContext(out MemoryStream body)
    {
        body = new MemoryStream();
        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.Path = "/api/chat";
        context.Response.Body = body;
        return context;
    }

    private static string ReadBody(MemoryStream body) =>
        System.Text.Encoding.UTF8.GetString(body.ToArray());

    private static DefaultHttpContext CreateStartedResponseContext()
    {
        var features = new FeatureCollection();
        features.Set<IHttpRequestFeature>(new HttpRequestFeature { Method = "GET", Path = "/api/chat/stream" });
        features.Set<IHttpResponseFeature>(new StartedResponseFeature());
        features.Set<IHttpResponseBodyFeature>(new StreamResponseBodyFeature(Stream.Null));
        return new DefaultHttpContext(features);
    }

    private sealed class StartedResponseFeature : IHttpResponseFeature
    {
        public Stream Body { get; set; } = Stream.Null;
        public bool HasStarted => true;
        public IHeaderDictionary Headers { get; set; } = new HeaderDictionary();
        public string? ReasonPhrase { get; set; }
        public int StatusCode { get; set; } = StatusCodes.Status200OK;
        public void OnCompleted(Func<object, Task> callback, object state) { }
        public void OnStarting(Func<object, Task> callback, object state) { }
    }

    private sealed record LogEntry(LogLevel Level, string Message);

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<LogEntry> Entries { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add(new LogEntry(logLevel, formatter(state, exception)));

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
