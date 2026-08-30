using System.Net;
using System.Text;
using LmKitOmniApi.Infrastructure.AI.Research;
using LmKitOmniApi.Infrastructure.AI.Security;
using Microsoft.Extensions.Logging.Abstractions;

namespace LmKitOmniApi.Tests;

/// <summary>
/// Pure unit tests for <see cref="ResearchContentFetcher"/> — no network, no
/// model: HTTP responses come from a stub <see cref="HttpMessageHandler"/> and
/// SSRF validation from a stub <see cref="IResearchUrlValidator"/>.
/// </summary>
public class ResearchContentFetcherTests
{
    private const string TestUrl = "https://example.com/article";

    // ─────────────────────────────────────────────
    // 1. HTML extraction
    // ─────────────────────────────────────────────

    [Fact]
    public async Task FetchAsync_HtmlPage_ExtractsBodyTextAndTitle_ExcludesScriptNavFooter()
    {
        const string html = """
            <html>
              <head>
                <title>  Trang   Thử Nghiệm </title>
                <script>var SECRET_SCRIPT_TOKEN = "leak";</script>
                <style>.hidden { color: STYLE_RULE_TEXT; }</style>
              </head>
              <body>
                <nav>NAV_MENU_TEXT</nav>
                <p>Nội dung chính của bài viết nghiên cứu.</p>
                <p>Đoạn văn thứ hai với thông tin quan trọng.</p>
                <script>console.log("INLINE_SCRIPT_TEXT");</script>
                <footer>FOOTER_COPYRIGHT_TEXT</footer>
              </body>
            </html>
            """;
        var (fetcher, handler) = CreateFetcher(HtmlResponse(html));

        var source = await fetcher.FetchAsync(TestUrl);

        Assert.NotNull(source);
        Assert.Equal(TestUrl, source!.Url);
        Assert.Equal("Trang Thử Nghiệm", source.Title);
        Assert.Contains("Nội dung chính của bài viết nghiên cứu.", source.Content);
        Assert.Contains("Đoạn văn thứ hai với thông tin quan trọng.", source.Content);
        Assert.DoesNotContain("SECRET_SCRIPT_TOKEN", source.Content);
        Assert.DoesNotContain("INLINE_SCRIPT_TEXT", source.Content);
        Assert.DoesNotContain("STYLE_RULE_TEXT", source.Content);
        Assert.DoesNotContain("NAV_MENU_TEXT", source.Content);
        Assert.DoesNotContain("FOOTER_COPYRIGHT_TEXT", source.Content);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task FetchAsync_HtmlPage_CollapsesWhitespaceRuns()
    {
        const string html = "<html><body><p>một   hai\n\n\t ba</p></body></html>";
        var (fetcher, _) = CreateFetcher(HtmlResponse(html));

        var source = await fetcher.FetchAsync(TestUrl);

        Assert.NotNull(source);
        Assert.Equal("một hai ba", source!.Content);
    }

    [Fact]
    public async Task FetchAsync_LongBody_RespectsExtractedCharCap()
    {
        // 20,000 visible chars — well under the 512 KB byte cap, well over the
        // 8,000-char extraction cap.
        var html = $"<html><head><title>Dài</title></head><body>{new string('a', 20_000)}</body></html>";
        var (fetcher, _) = CreateFetcher(HtmlResponse(html));

        var source = await fetcher.FetchAsync(TestUrl);

        Assert.NotNull(source);
        Assert.Equal(ResearchLimits.MaxExtractedCharsPerSource, source!.Content.Length);
    }

    // ─────────────────────────────────────────────
    // 2. Content-type allow-list
    // ─────────────────────────────────────────────

    [Theory]
    [InlineData("application/json")]
    [InlineData("application/pdf")]
    [InlineData("image/png")]
    [InlineData("application/octet-stream")]
    public async Task FetchAsync_NonTextContentType_IsRejected(string mediaType)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"not\":\"a web page\"}", Encoding.UTF8, mediaType)
        };
        var (fetcher, handler) = CreateFetcher(response);

        var source = await fetcher.FetchAsync(TestUrl);

        Assert.Null(source);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task FetchAsync_PlainText_IsAccepted()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("Văn bản  thuần túy\nvới nhiều dòng.", Encoding.UTF8, "text/plain")
        };
        var (fetcher, _) = CreateFetcher(response);

        var source = await fetcher.FetchAsync(TestUrl);

        Assert.NotNull(source);
        Assert.Equal("Văn bản thuần túy với nhiều dòng.", source!.Content);
        Assert.Equal("example.com", source.Title); // plain text has no <title>
    }

    // ─────────────────────────────────────────────
    // 3. Oversize body — truncated at the 512 KB cap, never throws
    // ─────────────────────────────────────────────

    [Fact]
    public async Task FetchAsync_OversizeBody_TruncatesAtByteCapWithoutThrowing()
    {
        // 2 MB body; the fetcher must stop reading at exactly 512 KB.
        var oversize = new byte[2 * 1024 * 1024];
        Array.Fill(oversize, (byte)'a');
        var countingStream = new CountingReadStream(new MemoryStream(oversize));

        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(countingStream)
        };
        response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/html");
        var (fetcher, _) = CreateFetcher(response);

        var source = await fetcher.FetchAsync(TestUrl);

        // No exception, a usable (capped) source, and the body read stopped at the cap.
        Assert.NotNull(source);
        Assert.True(source!.Content.Length <= ResearchLimits.MaxExtractedCharsPerSource);
        Assert.True(countingStream.TotalBytesRead <= ResearchLimits.MaxContentBytes,
            $"Read {countingStream.TotalBytesRead} bytes; cap is {ResearchLimits.MaxContentBytes}.");
    }

    // ─────────────────────────────────────────────
    // 4. Sandbox validation failures are skipped BEFORE any request is sent
    // ─────────────────────────────────────────────

    [Fact]
    public async Task FetchAsync_UrlFailingSandboxValidation_IsSkippedWithoutHttpRequest()
    {
        var handler = new StubHttpMessageHandler(_ => HtmlResponse("<html><body>should never be fetched</body></html>"));
        var fetcher = new ResearchContentFetcher(
            new HttpClient(handler),
            new StubUrlValidator(allow: false, denialReason: "Không cho phép truy cập mạng nội bộ hoặc metadata."),
            NullLogger<ResearchContentFetcher>.Instance);

        var source = await fetcher.FetchAsync("http://169.254.169.254/latest/meta-data/");

        Assert.Null(source);
        Assert.Equal(0, handler.RequestCount); // SSRF gate ran first — no socket, no request
    }

    [Fact]
    public async Task FetchAsync_HttpFailureStatus_IsSkippedNotThrown()
    {
        // AllowAutoRedirect=false means 3xx surfaces here as a non-success status.
        var (fetcher, _) = CreateFetcher(new HttpResponseMessage(HttpStatusCode.MovedPermanently));

        var source = await fetcher.FetchAsync(TestUrl);

        Assert.Null(source);
    }

    // ─────────────────────────────────────────────
    // Test doubles
    // ─────────────────────────────────────────────

    private static (ResearchContentFetcher Fetcher, StubHttpMessageHandler Handler) CreateFetcher(
        HttpResponseMessage response)
    {
        var handler = new StubHttpMessageHandler(_ => response);
        var fetcher = new ResearchContentFetcher(
            new HttpClient(handler),
            new StubUrlValidator(allow: true),
            NullLogger<ResearchContentFetcher>.Instance);
        return (fetcher, handler);
    }

    private static HttpResponseMessage HtmlResponse(string html) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(html, Encoding.UTF8, "text/html")
    };

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
        public int RequestCount { get; private set; }

        public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
            => _responder = responder;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(_responder(request));
        }
    }

    private sealed class StubUrlValidator : IResearchUrlValidator
    {
        private readonly bool _allow;
        private readonly string _denialReason;

        public StubUrlValidator(bool allow, string denialReason = "denied")
        {
            _allow = allow;
            _denialReason = denialReason;
        }

        public Task<PathValidationResult> ValidateAsync(string url, CancellationToken ct = default)
            => Task.FromResult(_allow
                ? PathValidationResult.Allow(url)
                : PathValidationResult.Deny(_denialReason));
    }

    /// <summary>Read-only stream wrapper counting how many bytes were actually read.</summary>
    private sealed class CountingReadStream : Stream
    {
        private readonly Stream _inner;
        public long TotalBytesRead { get; private set; }

        public CountingReadStream(Stream inner) => _inner = inner;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _inner.Length;
        public override long Position
        {
            get => _inner.Position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = _inner.Read(buffer, offset, count);
            TotalBytesRead += read;
            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await _inner.ReadAsync(buffer, cancellationToken);
            TotalBytesRead += read;
            return read;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
