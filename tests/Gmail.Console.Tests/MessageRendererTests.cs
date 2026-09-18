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
    public void StripTags_removes_markup_reverse_markdown_left_behind()
    {
        // A table nested in a table cell is passed through verbatim, so a mail protection
        // service's challenge page arrives as kilobytes of markup under --body markdown.
        var markdown = "| <table width=\"100%\"> <tr> <td> <h1>One click to deliver your email!</h1> "
                     + "<!-- note --> <p>Hello,</p> </td> </tr> </table> |";

        var stripped = MessageRenderer.StripTags(markdown);

        Assert.DoesNotContain("<table", stripped);
        Assert.DoesNotContain("<td", stripped);
        Assert.DoesNotContain("<!--", stripped);
        Assert.Contains("One click to deliver your email!", stripped);
        Assert.Contains("Hello,", stripped);
    }

    [Fact]
    public void StripTags_keeps_an_address_in_angle_brackets()
    {
        // The reason the pattern requires whitespace, '/' or '>' after the tag name.
        const string markdown = "Forwarded from Paul <pdougan@me.com>, see <https://example.com/a>.";

        Assert.Equal(markdown, MessageRenderer.StripTags(markdown));
    }

    [Fact]
    public void StripTags_does_not_flatten_paragraphs()
    {
        // Collapsing all whitespace here would run every message into one line.
        const string markdown = "First para.\n\nSecond para.";

        Assert.Equal(markdown, MessageRenderer.StripTags(markdown));
    }

    [Fact]
    public void TrimQuotedReply_handles_an_attribution_broken_across_lines()
    {
        // What Apple Mail and Outlook on the web actually produce: the attribution lives in a
        // table, and converting it to text leaves one sentence spread over indented fragments.
        var body = "Hi Paul,\n\nChose Mac here: https://filestar.com/download\n\n"
                 + "                            On\n"
                 + "                            Tue, 4 Aug at 10:59 AM\n"
                 + "                            ,  Paul Dougan <pdougan@me.com>  wrote:\n\n"
                 + "                             So what do I do about it?";

        var trimmed = MessageRenderer.TrimQuotedReply(body);

        Assert.Equal("Hi Paul,\n\nChose Mac here: https://filestar.com/download", trimmed);
    }

    [Fact]
    public void TrimQuotedReply_does_not_cut_prose_that_merely_starts_with_on()
    {
        // Same words, but a paragraph break and full-width lines mean this is someone talking,
        // not a quote header. Joining across it would delete the rest of their message.
        var body = "On Monday the conversion failed again.\n\n"
                 + "I asked your colleague about it and here is what they wrote:\n\n"
                 + "that it was a known issue.";

        Assert.Equal(body, MessageRenderer.TrimQuotedReply(body));
    }

    [Fact]
    public void TrimQuotedReply_handles_a_pasted_outlook_header_block()
    {
        var body = "Cancelled, thanks.\n\nFrom: Support <info@filestar.com>\nSent: 4 August 2026 10:59\n"
                 + "To: Paul\nSubject: Re: Plan\n\nOlder text.";

        Assert.Equal("Cancelled, thanks.", MessageRenderer.TrimQuotedReply(body));
    }

    [Fact]
    public void TrimQuotedReply_keeps_a_from_line_that_is_not_a_header_block()
    {
        // No Sent:/Date: follows, so this is a sentence about where a file came from.
        var body = "From: the scanner, the PDF is fine.\n\nIt is only Filestar that rejects it.";

        Assert.Equal(body, MessageRenderer.TrimQuotedReply(body));
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
