using System.Text;
using System.Text.RegularExpressions;
using HtmlAgilityPack;

namespace LmKitOmniApi.Infrastructure.AI.Web;

/// <summary>
/// Turns an HTML document into the readable text of its main content — the fallback extraction
/// used when the primary LM-Kit reader cannot produce content (see <see cref="LmKitWebPageReader"/>).
///
/// <para>
/// <b>Why a second extractor at all.</b> The primary path (<see cref="LMKit.Agents.Tools.BuiltIn.Net.WebReadTool"/>)
/// extracts better content from the pages it can read, and it stays the primary path. But it has no
/// cookie jar, so a page behind a JavaScript cookie challenge fails there — and the remedy is a
/// retry that already holds the body, which means this codebase owns the HTML for once and has to
/// turn it into text itself. Today that choice is between "extract it here" and "tell the user the
/// link could not be read", and the second is the failure this type removes.
/// </para>
///
/// <para>
/// HtmlAgilityPack (already a dependency, already pure managed code) does the parsing, so this is
/// exercised in CI against real captured pages rather than only on a live host.
/// </para>
/// </summary>
public static partial class WebPageTextExtractor
{
    /// <summary>Structural and non-content elements: never part of a page's readable text.</summary>
    private static readonly HashSet<string> DroppedTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "style", "noscript", "iframe", "svg", "form", "nav", "header", "footer",
        "aside", "button", "template", "object", "embed", "select", "option", "canvas",
    };

    /// <summary>Elements that end a line of text, so paragraph structure survives flattening.</summary>
    private static readonly HashSet<string> BlockTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "p", "div", "br", "li", "tr", "td", "th", "h1", "h2", "h3", "h4", "h5", "h6",
        "section", "article", "blockquote", "pre", "figcaption", "dd", "dt", "ul", "ol", "table",
    };

    /// <summary>Container elements a page's real content is likely to sit in, best first.</summary>
    private static readonly string[] ContentRoots =
    [
        "//article",
        "//*[@role='main']",
        "//main",
    ];

    /// <summary>
    /// A root has to hold at least this much text before it is preferred over <c>&lt;body&gt;</c>:
    /// a teaser card in a related-links rail is an <c>&lt;article&gt;</c> too, and picking one would
    /// return a 40-word stub in place of the page.
    /// </summary>
    private const int MinRootTextChars = 400;

    /// <summary>
    /// Readable text of a fetched response: <paramref name="body"/> decoded with the charset the
    /// server declared in <paramref name="contentType"/>, then extracted. The byte overload exists
    /// because the gated fetcher hands back bytes, and an undecoded byte array is where a Vietnamese
    /// page turns into mojibake.
    /// </summary>
    public static string Extract(byte[]? body, string? contentType)
    {
        if (body is null || body.Length == 0)
            return string.Empty;

        return Extract(DecodeBody(body, contentType));
    }

    /// <summary>Readable text of <paramref name="html"/>, paragraphs on their own lines. Empty when nothing readable remains.</summary>
    public static string Extract(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return string.Empty;

        // A response that is not HTML at all (JSON, plain text, an error page) must not be mangled
        // into an empty string: HtmlAgilityPack would happily treat the whole payload as one text
        // node, which is exactly the right outcome, but only when it really has no markup.
        var document = new HtmlDocument();
        try
        {
            document.LoadHtml(html);
        }
        catch (Exception)
        {
            // A parser that cannot build a tree still leaves readable text behind: keep it.
            return Normalize(html);
        }

        var dropped = document.DocumentNode.SelectNodes(
            "//script|//style|//noscript|//iframe|//svg|//form|//nav|//header|//footer|//aside|//button|//template|//object|//embed|//select|//canvas");
        if (dropped is not null)
        {
            foreach (var node in dropped)
                node.Remove();
        }

        var root = PickContentRoot(document);
        var text = new StringBuilder();
        AppendText(root, text);
        return Normalize(text.ToString());
    }

    /// <summary>
    /// The node whose text is the page: the richest content container when it is substantive,
    /// otherwise the whole body.
    /// </summary>
    private static HtmlNode PickContentRoot(HtmlDocument document)
    {
        HtmlNode? best = null;
        var bestLength = 0;

        foreach (var xpath in ContentRoots)
        {
            var candidates = document.DocumentNode.SelectNodes(xpath);
            if (candidates is null)
                continue;

            foreach (var candidate in candidates)
            {
                var length = TextLengthOf(candidate);
                if (length > bestLength)
                {
                    best = candidate;
                    bestLength = length;
                }
            }
        }

        if (best is not null && bestLength >= MinRootTextChars)
            return best;

        return document.DocumentNode.SelectSingleNode("//body") ?? document.DocumentNode;
    }

    /// <summary>
    /// Length of the text a node would contribute, discounting the containers this extractor drops:
    /// a candidate whose bulk is a <c>&lt;script&gt;</c> or a nav rail must not win the vote.
    /// </summary>
    private static int TextLengthOf(HtmlNode node)
    {
        var length = 0;
        var stack = new Stack<HtmlNode>();
        stack.Push(node);

        while (stack.Count > 0)
        {
            var current = stack.Pop();
            foreach (var child in current.ChildNodes)
            {
                if (child.NodeType == HtmlNodeType.Text)
                {
                    length += child.InnerText.Length;
                    continue;
                }

                if (child.NodeType == HtmlNodeType.Element && !DroppedTags.Contains(child.Name))
                    stack.Push(child);
            }
        }

        return length;
    }

    /// <summary>Walks the tree appending text, with a line break around every block element.</summary>
    private static void AppendText(HtmlNode node, StringBuilder builder)
    {
        foreach (var child in node.ChildNodes)
        {
            if (child.NodeType == HtmlNodeType.Text)
            {
                builder.Append(child.InnerText);
                continue;
            }

            if (child.NodeType != HtmlNodeType.Element || DroppedTags.Contains(child.Name))
                continue;

            var isBlock = BlockTags.Contains(child.Name);
            if (isBlock)
                builder.Append('\n');

            AppendText(child, builder);

            if (isBlock)
                builder.Append('\n');
        }
    }

    /// <summary>
    /// Decodes a response body using the server's declared charset, falling back to UTF-8. The
    /// charset is read from the FIRST token only: real servers send malformed values such as
    /// <c>text/html; charset=utf-8,gbk</c> (laodong.vn does), and treating that whole string as an
    /// encoding name throws. An unknown-but-valid name is tried once and then abandoned for UTF-8,
    /// which loses nothing here — decoding never throws, it substitutes.
    /// </summary>
    public static string DecodeBody(byte[] body, string? contentType)
    {
        var encoding = ResolveEncoding(contentType);
        return encoding.GetString(body);
    }

    private static Encoding ResolveEncoding(string? contentType)
    {
        var match = CharsetPattern().Match(contentType ?? string.Empty);
        if (match.Success)
        {
            try
            {
                return Encoding.GetEncoding(match.Groups["charset"].Value);
            }
            catch (ArgumentException)
            {
                // Not an encoding this host knows (or one needing a codepage provider). Below: UTF-8.
            }
        }

        // UTF-8 without a byte-order-mark write AND without throwing on invalid bytes: a legacy
        // page decoded as UTF-8 comes back with replacement characters, never an exception.
        return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);
    }

    [GeneratedRegex("charset\\s*=\\s*[\"']?\\s*(?<charset>[A-Za-z0-9_-]+)", RegexOptions.IgnoreCase)]
    private static partial Regex CharsetPattern();

    /// <summary>Collapses the whitespace the markup left behind into one line per paragraph.</summary>
    private static string Normalize(string text)
    {
        var lines = new List<string>();
        foreach (var raw in text.Replace('\r', '\n').Split('\n'))
        {
            var line = CollapseSpaces(raw);
            if (line.Length > 0)
                lines.Add(line);
        }

        return string.Join('\n', lines);
    }

    private static string CollapseSpaces(string value)
    {
        var builder = new StringBuilder(value.Length);
        var pendingSpace = false;

        foreach (var character in value)
        {
            if (char.IsWhiteSpace(character))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(character);
        }

        return builder.ToString();
    }
}
