using Gmail.Console.Mail;

namespace Gmail.Console.Tests;

public class MessageRendererTests
{
    [Fact]
    public void Truncate_leaves_short_bodies_alone()
    {
        var (text, omitted, _) = MessageRenderer.Truncate("short", 100);

        Assert.Equal("short", text);
        Assert.Equal(0, omitted);
    }

    [Fact]
    public void Truncate_is_disabled_by_zero()
    {
        var body = new string('x', 5000);
        var (text, omitted, _) = MessageRenderer.Truncate(body, 0);

        Assert.Equal(body, text);
        Assert.Equal(0, omitted);
    }

    [Fact]
    public void Truncate_cuts_on_a_paragraph_boundary()
    {
        var body = "First paragraph.\n\n" + new string('a', 60) + "\n\n" + new string('b', 400);
        var (text, omitted, total) = MessageRenderer.Truncate(body, 120);

        Assert.EndsWith(new string('a', 60), text);
        Assert.True(omitted > 0);
        Assert.Equal(body.Length, total);
    }

    [Fact]
    public void Truncate_falls_back_to_a_hard_cut_when_there_is_no_boundary()
    {
        var body = new string('a', 500);
        var (text, omitted, _) = MessageRenderer.Truncate(body, 100);

        Assert.Equal(100, text.Length);
        Assert.Equal(400, omitted);
    }

    [Fact]
    public void TrimQuotedReply_drops_the_attribution_and_everything_after()
    {
        var body = """
            Thanks, that works for me.

            On Fri, 28 Aug 2026 at 09:12 UTC, Alice <alice@example.com> wrote:
            > Are you free on Tuesday?
            > Alice
            """;

        var trimmed = MessageRenderer.TrimQuotedReply(body);

        Assert.Equal("Thanks, that works for me.", trimmed);
    }

    [Fact]
    public void TrimQuotedReply_handles_the_outlook_separator()
    {
        var body = "Agreed.\n\n-----Original Message-----\nFrom: Alice\n> old text";

        Assert.Equal("Agreed.", MessageRenderer.TrimQuotedReply(body));
    }

    [Fact]
    public void TrimQuotedReply_keeps_a_body_with_no_quoted_section()
    {
        const string body = "Just a normal message.\n\nWith two paragraphs.";

        Assert.Equal(body, MessageRenderer.TrimQuotedReply(body));
    }

    [Fact]
    public void TrimQuotedReply_keeps_the_signature_block()
    {
        var body = "Here you go.\n\n-- \nNiels\n+46 70 000 00 00";

        Assert.Contains("+46 70 000 00 00", MessageRenderer.TrimQuotedReply(body));
    }

    [Fact]
    public void Normalize_collapses_blank_runs_and_strips_trailing_space()
    {
        var normalized = MessageRenderer.Normalize("one   \r\n\r\n\r\n\r\n\r\ntwo\t\r\n");

        Assert.Equal("one\n\n\ntwo", normalized);
        Assert.DoesNotContain('\r', normalized);
    }

    [Fact]
    public void HtmlToMarkdown_drops_style_and_script_content()
    {
        var html = "<html><head><style>.a{color:red}</style></head>" +
                   "<body><script>alert(1)</script><p>Hello <a href=\"https://x.test\">link</a></p></body></html>";

        var markdown = MessageRenderer.HtmlToMarkdown(html);

        Assert.DoesNotContain("color:red", markdown);
        Assert.DoesNotContain("alert(1)", markdown);
        Assert.Contains("https://x.test", markdown);
    }
}
