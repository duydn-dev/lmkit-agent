using LmKitOmniApi.Infrastructure.AI.ComputerUse;

namespace LmKitOmniApi.Tests;

/// <summary>
/// Pure unit tests for <see cref="ComputerUseActionParser"/>: it must parse well-formed
/// JSON actions (bare, markdown-fenced, or wrapped in prose), tolerate the small shape
/// variations local models produce, and REJECT anything malformed/unknown/under-specified
/// safely (false + reason, never a thrown exception, never a guessed side-effecting action).
/// </summary>
public class ComputerUseActionParserTests
{
    private static ComputerUseAction Parse(string raw)
    {
        Assert.True(ComputerUseActionParser.TryParse(raw, out var action, out var error),
            $"Expected parse to succeed but it failed: {error}");
        Assert.NotNull(action);
        return action!;
    }

    private static void AssertRejected(string? raw)
    {
        var ok = ComputerUseActionParser.TryParse(raw, out var action, out var error);
        Assert.False(ok, "Expected parse to be rejected.");
        Assert.Null(action);
        Assert.False(string.IsNullOrWhiteSpace(error), "A rejection must carry a reason.");
    }

    // ── Valid actions ──

    [Fact]
    public void Parses_Navigate()
    {
        var a = Parse("{\"action\":\"navigate\",\"url\":\"https://example.com/x\"}");
        Assert.Equal(ComputerUseActionType.Navigate, a.Type);
        Assert.Equal("https://example.com/x", a.Url);
        Assert.True(a.IsSideEffecting);
    }

    [Fact]
    public void Parses_ClickByRef()
    {
        var a = Parse("{\"action\":\"click\",\"ref\":7}");
        Assert.Equal(ComputerUseActionType.Click, a.Type);
        Assert.Equal(7, a.Ref);
        Assert.True(a.IsSideEffecting);
    }

    [Fact]
    public void Parses_ClickByCoordinates()
    {
        var a = Parse("{\"action\":\"click\",\"x\":10,\"y\":20}");
        Assert.Equal(ComputerUseActionType.Click, a.Type);
        Assert.Equal(10, a.X);
        Assert.Equal(20, a.Y);
    }

    [Fact]
    public void Parses_TypeWithRefAndText()
    {
        var a = Parse("{\"action\":\"type\",\"ref\":3,\"text\":\"hello world\"}");
        Assert.Equal(ComputerUseActionType.Type, a.Type);
        Assert.Equal(3, a.Ref);
        Assert.Equal("hello world", a.Text);
    }

    [Fact]
    public void Parses_Key_Scroll_Wait_Screenshot()
    {
        Assert.Equal("Enter", Parse("{\"action\":\"key\",\"keys\":\"Enter\"}").Keys);

        var scroll = Parse("{\"action\":\"scroll\",\"direction\":\"down\",\"amount\":5}");
        Assert.Equal(ComputerUseActionType.Scroll, scroll.Type);
        Assert.Equal("down", scroll.Direction);
        Assert.Equal(5, scroll.Amount);
        Assert.False(scroll.IsSideEffecting);

        Assert.Equal(750, Parse("{\"action\":\"wait\",\"ms\":750}").Ms);
        Assert.Equal(ComputerUseActionType.Screenshot, Parse("{\"action\":\"screenshot\"}").Type);
    }

    [Fact]
    public void Parses_Done_And_Ask_AsTerminal()
    {
        var done = Parse("{\"action\":\"done\",\"summary\":\"all set\"}");
        Assert.Equal(ComputerUseActionType.Done, done.Type);
        Assert.Equal("all set", done.Summary);
        Assert.True(done.IsTerminal);

        var ask = Parse("{\"action\":\"ask\",\"question\":\"which one?\"}");
        Assert.Equal(ComputerUseActionType.Ask, ask.Type);
        Assert.Equal("which one?", ask.Question);
        Assert.True(ask.IsTerminal);
    }

    // ── Tolerance: fences, prose, wrappers, aliases, string numbers ──

    [Fact]
    public void Parses_ThroughMarkdownFence()
    {
        var raw = "Here is my next step:\n```json\n{\"action\":\"click\",\"ref\":2}\n```\nThat's it.";
        var a = Parse(raw);
        Assert.Equal(ComputerUseActionType.Click, a.Type);
        Assert.Equal(2, a.Ref);
    }

    [Fact]
    public void Parses_WithSurroundingProse()
    {
        var a = Parse("I think we should {\"action\":\"navigate\",\"url\":\"https://a.test/\"} now.");
        Assert.Equal("https://a.test/", a.Url);
    }

    [Fact]
    public void Parses_NestedParamsWrapper()
    {
        var a = Parse("{\"action\":\"type\",\"params\":{\"ref\":4,\"text\":\"x\"}}");
        Assert.Equal(4, a.Ref);
        Assert.Equal("x", a.Text);
    }

    [Fact]
    public void Parses_VerbFromTypeField_AndStringRef()
    {
        // Some models put the verb in a "type" field and deliver ref as a string.
        var a = Parse("{\"type\":\"click\",\"ref\":\"9\"}");
        Assert.Equal(ComputerUseActionType.Click, a.Type);
        Assert.Equal(9, a.Ref);
    }

    [Fact]
    public void Parses_Aliases()
    {
        Assert.Equal(ComputerUseActionType.Navigate, Parse("{\"action\":\"goto\",\"url\":\"https://a.test/\"}").Type);
        Assert.Equal(ComputerUseActionType.Done, Parse("{\"action\":\"finish\",\"summary\":\"x\"}").Type);
        Assert.Equal(ComputerUseActionType.Ask, Parse("{\"action\":\"handoff\",\"question\":\"x\"}").Type);
    }

    [Fact]
    public void Parses_ObjectWhoseStringValueContainsBraces()
    {
        // The balanced-brace extractor must respect string literals, not stop at "{b}".
        var a = Parse("{\"action\":\"type\",\"ref\":1,\"text\":\"a {b} c\"}");
        Assert.Equal("a {b} c", a.Text);
    }

    // ── Rejections (safe, no throw) ──

    [Fact]
    public void Rejects_EmptyOrWhitespace()
    {
        AssertRejected(null);
        AssertRejected("");
        AssertRejected("   ");
    }

    [Fact]
    public void Rejects_NoJsonObject()
    {
        AssertRejected("just some prose without any json");
    }

    [Fact]
    public void Rejects_MalformedJson()
    {
        AssertRejected("{\"action\": \"click\", \"ref\": }");     // dangling value
        AssertRejected("{\"action\":\"click\",");                  // unterminated
    }

    [Fact]
    public void Rejects_UnknownAction()
    {
        AssertRejected("{\"action\":\"launch_missiles\"}");
        AssertRejected("{\"action\":\"execute\",\"code\":\"rm -rf\"}");
    }

    [Fact]
    public void Rejects_MissingVerb()
    {
        AssertRejected("{\"ref\":3,\"text\":\"x\"}");
    }

    [Fact]
    public void Rejects_UnderSpecifiedActions()
    {
        AssertRejected("{\"action\":\"navigate\"}");               // no url
        AssertRejected("{\"action\":\"click\"}");                  // no ref, no coords
        AssertRejected("{\"action\":\"type\",\"ref\":1}");         // no text
        AssertRejected("{\"action\":\"type\",\"text\":\"x\"}");    // no target
        AssertRejected("{\"action\":\"key\"}");                    // no keys
    }

    [Fact]
    public void Rejects_NonObjectJson()
    {
        AssertRejected("[1,2,3]");
        AssertRejected("\"just a string\"");
    }
}
