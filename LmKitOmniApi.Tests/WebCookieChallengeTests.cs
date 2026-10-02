using LmKitOmniApi.Infrastructure.AI.Web;

namespace LmKitOmniApi.Tests;

/// <summary>
/// Regression tests for the JavaScript cookie challenge that made a readable article unreadable.
///
/// <remarks>
/// The bodies here are the REAL ones captured from the live failure: <c>ChallengeBody</c> is
/// laodong.vn's 177-byte answer to a cookieless GET of
/// <c>https://laodong.vn/kinh-doanh/gia-xang-dau-hom-nay-210-tang-manh-hon-4-1776264.ldo</c>, and
/// <c>ArticleBody</c> is the same URL's answer once the cookie it hands out is sent back (111 KB of
/// markup, reduced here to the fragments this test needs). The agent's reply to that link was
/// "Tôi không thể truy cập nội dung từ đường link bạn cung cấp", followed by a request that the user
/// paste the article — so these tests pin both halves of the fix: recognize the stub, and recognize
/// it only when it is a stub.
/// </remarks>
/// </summary>
public sealed class WebCookieChallengeTests
{
    private const string ChallengeBody =
        "<html><body><script>document.cookie=\"D1N=2ee4c4918e100cc9b65bb48ca3405b09\"+\"; " +
        "expires=Fri, 31 Dec 2099 23:59:59 GMT; path=/\";window.location.reload(true);</script></body></html>";

    [Fact]
    public void TheLiveChallenge_YieldsTheCookieToSendBack()
    {
        Assert.Equal("D1N=2ee4c4918e100cc9b65bb48ca3405b09", WebCookieChallenge.TryExtractCookie(ChallengeBody));
    }

    [Theory]
    // Single quotes, surrounding whitespace and a longer escape chain: the same challenge written
    // by a different template.
    [InlineData("<script>document.cookie = 'visitor_id=9f2a71cc4b';window.location.reload();</script>")]
    [InlineData("<script>document . cookie=\"cf_clearance=abc123def456ghi\";location.reload(1);</script>")]
    public void ChallengeVariants_AreRecognized(string body) =>
        Assert.NotNull(WebCookieChallenge.TryExtractCookie(body));

    /// <summary>
    /// The negative half matters more than the positive one: treating a real article as a challenge
    /// would re-fetch every page and hand a third-party cookie to a site that asked for none.
    /// </summary>
    [Theory]
    // A reload script with no cookie to echo back — nothing to retry with.
    [InlineData("<script>window.location.reload(true);</script>")]
    // An article that merely MENTIONS document.cookie: the assignment is prose, not a challenge.
    [InlineData("<p>Cách đặt cookie: dùng document.cookie = \"name=value\" trong trình duyệt.</p>")]
    // A consent script that sets a cookie and reloads nothing: a page that renders normally
    // afterwards, so a retry with the cookie would be pure waste.
    [InlineData("<html><body><article><p>Giá xăng dầu hôm nay tăng mạnh hơn 4%.</p></article>" +
        "<script>document.cookie=\"cookieconsent=accepted\";</script></body></html>")]
    [InlineData("")]
    public void NotAChallenge_IsLeftAlone(string body) =>
        Assert.Null(WebCookieChallenge.TryExtractCookie(body));

    [Fact]
    public void NullBody_IsNotAChallenge() =>
        Assert.Null(WebCookieChallenge.TryExtractCookie(null));

    [Fact]
    public void AnOversizedBody_IsNeverTreatedAsAStub() =>
        Assert.Null(WebCookieChallenge.TryExtractCookie(
            "<script>document.cookie=\"sid=0123456789abcdef\";location.reload();</script>"
            + new string('x', 8192)));
}
