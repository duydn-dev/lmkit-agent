using System.Text.RegularExpressions;

namespace LmKitOmniApi.Infrastructure.AI.Web;

/// <summary>
/// Recognizes the JavaScript cookie "challenge" a number of sites put in front of every article,
/// and pulls the cookie the page wants back out of it.
///
/// <para>
/// <b>Why this exists.</b> A live turn ended with the agent telling the user "Tôi không thể truy
/// cập nội dung từ đường link bạn cung cấp" for a perfectly readable article. The link was
/// <c>laodong.vn/kinh-doanh/gia-xang-dau-hom-nay-…</c>, and the server answered every plain HTTP
/// GET with 177 bytes of HTML:
/// </para>
/// <code>
/// &lt;html&gt;&lt;body&gt;&lt;script&gt;document.cookie="D1N=2ee4c4918e100cc9b65bb48ca3405b09"+";
/// expires=Fri, 31 Dec 2099 23:59:59 GMT; path=/";window.location.reload(true);&lt;/script&gt;&lt;/body&gt;&lt;/html&gt;
/// </code>
/// <para>
/// That is a challenge, not an article: the page sets a cookie and reloads, and only the request
/// that belongs to a cookie-carrying client gets the real content. Nothing here is a browser
/// feature — a request that already carries <c>D1N=…</c> receives the full 111 KB article. A
/// fetcher with no cookie jar (which is every fetcher in this codebase, deliberately) therefore
/// needs exactly one retry, and this type decides whether the response on hand is that challenge
/// and which cookie to send back.
/// </para>
///
/// <para>
/// <b>Deliberately conservative.</b> The body must be small (a real challenge stub is a few
/// hundred bytes — an article never is), must contain a cookie assignment, and must reload the
/// page. A page that merely mentions <c>document.cookie</c> inside a large document is not
/// treated as a challenge, so nothing gets re-fetched on a normal read.
/// </para>
/// </summary>
public static partial class WebCookieChallenge
{
    /// <summary>
    /// Upper bound on a body that can be a challenge stub. Real stubs are &lt; 1 KB; the bound is
    /// generous so a site can add attributes, and small enough that no article page qualifies.
    /// </summary>
    private const int MaxChallengeBodyChars = 8192;

    /// <summary>
    /// The cookie the challenge wants echoed back, as a <c>name=value</c> pair ready for a
    /// <c>Cookie</c> header — or <see langword="null"/> when the body is not a challenge.
    /// </summary>
    public static string? TryExtractCookie(string? body)
    {
        if (string.IsNullOrWhiteSpace(body) || body.Length > MaxChallengeBodyChars)
            return null;

        // Both halves must be present: the assignment alone ("we use cookies" prose, a snippet in
        // the middle of an article) and the reload alone (any redirect script) are each common.
        if (!ReloadPattern().IsMatch(body))
            return null;

        var match = CookieAssignmentPattern().Match(body);
        if (!match.Success)
            return null;

        var name = match.Groups["name"].Value;
        var value = match.Groups["value"].Value;
        return name.Length == 0 || value.Length == 0 ? null : $"{name}={value}";
    }

    /// <summary>
    /// <c>document.cookie = "NAME=VALUE"…</c>, with the optional escapes and either quote style a
    /// site may use. The opening quote is optional too: a challenge rendered inside a larger script
    /// string can arrive as <c>document.cookie=\"D1N=…\"</c>.
    /// </summary>
    [GeneratedRegex(
        @"document\s*\.\s*cookie\s*=\s*\\?[""']?(?<name>[A-Za-z0-9_\-]{1,32})=(?<value>[A-Za-z0-9_\-%]{6,})",
        RegexOptions.IgnoreCase)]
    private static partial Regex CookieAssignmentPattern();

    /// <summary>The reload half of the challenge: the page cannot show content until the cookie is set.</summary>
    [GeneratedRegex(@"(?:window\s*\.\s*)?location\s*\.\s*reload\s*\(", RegexOptions.IgnoreCase)]
    private static partial Regex ReloadPattern();
}
