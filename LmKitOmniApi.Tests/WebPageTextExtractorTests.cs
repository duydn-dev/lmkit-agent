using System.Text;
using LmKitOmniApi.Infrastructure.AI.Web;

namespace LmKitOmniApi.Tests;

/// <summary>
/// Tests for the extraction the cookie-challenge retry falls back on. It only ever runs where the
/// pipeline previously gave up ("[WebRead] Không đọc được trang"), so its job is narrow: return the
/// article's text, in Vietnamese, with paragraph structure and without the chrome around it.
/// </summary>
public sealed class WebPageTextExtractorTests
{
    /// <summary>The mojibake check, with the article title that was actually behind the link.</summary>
    [Fact]
    public void Utf8Pages_KeepVietnameseText()
    {
        const string title = "Giá xăng dầu hôm nay 2.10: Tăng mạnh hơn 4%";
        var html = $"<html><body><article><h1>{title}</h1><p>Giá xăng dầu thế giới tăng lên 71,2 USD/thùng.</p></article></body></html>";

        var text = WebPageTextExtractor.Extract(Encoding.UTF8.GetBytes(html), "text/html; charset=utf-8");

        Assert.Contains(title, text, StringComparison.Ordinal);
        Assert.Contains("71,2 USD/thùng", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// The charset header laodong.vn actually sends (a comma-separated charset list) must not throw
    /// or fall through to a wrong code page: the first token is the real one.
    /// </summary>
    [Fact]
    public void MalformedCharsetList_UsesTheFirstToken()
    {
        var html = "<html><body><article><p>Giá xăng dầu hôm nay</p></article></body></html>";

        var text = WebPageTextExtractor.Extract(Encoding.UTF8.GetBytes(html), "text/html; charset=utf-8,gbk");

        Assert.Contains("Giá xăng dầu hôm nay", text, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownCharset_FallsBackToUtf8InsteadOfThrowing()
    {
        var html = "<html><body><article><p>xăng dầu</p></article></body></html>";

        var text = WebPageTextExtractor.Extract(Encoding.UTF8.GetBytes(html), "text/html; charset=not-a-real-charset");

        Assert.Contains("xăng dầu", text, StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyOrMissingBody_IsEmpty()
    {
        Assert.Equal(string.Empty, WebPageTextExtractor.Extract(Array.Empty<byte>(), "text/html"));
        Assert.Equal(string.Empty, WebPageTextExtractor.Extract((byte[]?)null, null));
        Assert.Equal(string.Empty, WebPageTextExtractor.Extract((string?)null));
    }

    /// <summary>
    /// The article, not the page furniture: scripts, navigation, the related-links rail and the
    /// footer are all present in the markup and none of them may reach the model.
    /// </summary>
    [Fact]
    public void ScriptsNavAndFooter_AreDropped_AndTheArticleIsKept()
    {
        var article = string.Join(" ", Enumerable.Repeat(
            "Giá xăng dầu trong nước được điều chỉnh tăng mạnh hơn 4% trong kỳ điều hành hôm nay.", 6));
        var html = $$"""
            <html><head><title>Trang chủ - Báo Lao Động</title>
            <script>var _gaq=[['_setAccount','UA-0']];gtag('config','G-0');</script>
            <style>.ads{display:none}</style></head>
            <body>
            <nav><a href="/">Trang chủ</a><a href="/kinh-doanh">Kinh doanh</a></nav>
            <main><article>
            <h1>Giá xăng dầu hôm nay 2.10: Tăng mạnh hơn 4%</h1>
            <p>{{article}}</p>
            <p>Theo dữ liệu của Bộ Công Thương, giá xăng E5 RON 92 tăng 1.050 đồng/lít.</p>
            </article></main>
            <aside><h3>Tin liên quan</h3><p>Dự báo giá vàng tuần tới</p></aside>
            <footer>Báo Lao Động - Cơ quan của Tổng Liên đoàn Lao động Việt Nam</footer>
            </body></html>
            """;

        var text = WebPageTextExtractor.Extract(html);

        Assert.Contains("Giá xăng dầu hôm nay 2.10: Tăng mạnh hơn 4%", text, StringComparison.Ordinal);
        Assert.Contains("giá xăng E5 RON 92 tăng 1.050 đồng/lít", text, StringComparison.Ordinal);
        Assert.DoesNotContain("gtag", text, StringComparison.Ordinal);
        Assert.DoesNotContain("display:none", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Dự báo giá vàng", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Tổng Liên đoàn Lao động", text, StringComparison.Ordinal);
        Assert.DoesNotContain("<p>", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Paragraph structure survives: a flattened article is a wall of text no model can quote.
    /// </summary>
    [Fact]
    public void Paragraphs_StayOnSeparateLines()
    {
        var html = "<html><body><article><p>Đoạn một về giá xăng.</p><p>Đoạn hai về giá dầu.</p></article></body></html>";

        var text = WebPageTextExtractor.Extract(html);
        var lines = text.Split('\n');

        Assert.Contains("Đoạn một về giá xăng.", lines);
        Assert.Contains("Đoạn hai về giá dầu.", lines);
    }

    /// <summary>
    /// A page of nothing but chrome is not an article: the fallback must prefer the whole body over
    /// a 40-word related-links card, since picking the card would return a stub in place of the page.
    /// </summary>
    [Fact]
    public void ThinArticleCard_DoesNotWinOverTheBody()
    {
        var bodyText = string.Join(" ", Enumerable.Repeat("Nội dung chính của trang này khá dài và đầy đủ.", 20));
        var html = $"""
            <html><body><div>{bodyText}</div><article><h3>Tin liên quan</h3><p>Ngắn.</p></article></body></html>
            """;

        var text = WebPageTextExtractor.Extract(html);

        Assert.Contains("Nội dung chính của trang này khá dài", text, StringComparison.Ordinal);
        Assert.Contains("Tin liên quan", text, StringComparison.Ordinal);
    }

    /// <summary>A response that is not HTML at all (an error page, plain text, JSON) keeps its text.</summary>
    [Fact]
    public void NonHtmlPayload_KeepsItsText()
    {
        Assert.Equal("404 Not Found", WebPageTextExtractor.Extract("404 Not Found"));
    }
}
