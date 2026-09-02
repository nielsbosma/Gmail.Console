using Gmail.Console.Mail;
using MimeKit;

namespace Gmail.Console.Tests;

/// <summary>
/// Reply header derivation and recipient math — get this wrong and the thread forks in the
/// recipient's client, which is invisible from our side.
/// </summary>
public class ReplyBuilderTests
{
    private static MimeMessage Parent(
        string from = "Alice <alice@example.com>",
        string to = "me@example.com, Bob <bob@example.com>",
        string? cc = null,
        string? replyTo = null,
        string subject = "Quarterly numbers",
        string messageId = "parent-1@example.com",
        string[]? references = null)
    {
        var message = new MimeMessage { Subject = subject, MessageId = messageId };
        message.From.AddRange(InternetAddressList.Parse(from));
        message.To.AddRange(InternetAddressList.Parse(to));
        if (cc is not null) message.Cc.AddRange(InternetAddressList.Parse(cc));
        if (replyTo is not null) message.ReplyTo.AddRange(InternetAddressList.Parse(replyTo));
        foreach (var reference in references ?? []) message.References.Add(reference);
        message.Body = new TextPart("plain") { Text = "The numbers are attached." };
        return message;
    }

    [Theory]
    [InlineData("Quarterly numbers", "Re: Quarterly numbers")]
    [InlineData("Re: Quarterly numbers", "Re: Quarterly numbers")]
    [InlineData("RE: Re: Quarterly numbers", "Re: Quarterly numbers")]
    [InlineData("SV: Quarterly numbers", "Re: Quarterly numbers")]
    [InlineData("AW: SV: Quarterly numbers", "Re: Quarterly numbers")]
    [InlineData("Re[2]: Quarterly numbers", "Re: Quarterly numbers")]
    [InlineData("", "Re: ")]
    public void Subject_carries_exactly_one_prefix(string parent, string expected) =>
        Assert.Equal(expected, ReplyBuilder.Subject(parent));

    [Fact]
    public void Reply_goes_to_the_sender()
    {
        var reply = ReplyBuilder.Build(Parent(), "me@example.com", replyAll: false);

        Assert.Single(reply.To);
        Assert.Contains("alice@example.com", reply.To[0]);
        Assert.Empty(reply.Cc);
    }

    [Fact]
    public void Reply_to_header_wins_over_from()
    {
        var reply = ReplyBuilder.Build(
            Parent(replyTo: "Support <support@example.com>"), "me@example.com", replyAll: false);

        Assert.Contains("support@example.com", reply.To[0]);
    }

    [Fact]
    public void Reply_all_adds_the_other_recipients_but_never_ourselves()
    {
        var reply = ReplyBuilder.Build(
            Parent(cc: "carol@example.com"), "me@example.com", replyAll: true);

        var everyone = reply.To.Concat(reply.Cc).ToList();
        Assert.Contains(everyone, a => a.Contains("bob@example.com"));
        Assert.Contains(everyone, a => a.Contains("carol@example.com"));
        Assert.DoesNotContain(everyone, a => a.Contains("me@example.com"));
    }

    [Fact]
    public void Reply_all_does_not_repeat_the_sender_in_cc()
    {
        var reply = ReplyBuilder.Build(
            Parent(to: "me@example.com, alice@example.com"), "me@example.com", replyAll: true);

        Assert.Contains("alice@example.com", reply.To[0]);
        Assert.DoesNotContain(reply.Cc, a => a.Contains("alice@example.com"));
    }

    [Fact]
    public void References_chain_appends_the_parent()
    {
        var reply = ReplyBuilder.Build(
            Parent(references: ["root@example.com", "second@example.com"]), "me@example.com", replyAll: false);

        Assert.Equal(["root@example.com", "second@example.com", "parent-1@example.com"], reply.References);
        Assert.Equal("parent-1@example.com", reply.InReplyTo);
    }

    [Fact]
    public void References_does_not_duplicate_a_parent_already_in_the_chain()
    {
        var reply = ReplyBuilder.Build(
            Parent(references: ["parent-1@example.com"]), "me@example.com", replyAll: false);

        Assert.Equal(["parent-1@example.com"], reply.References);
    }

    [Fact]
    public void Replying_to_our_own_message_keeps_the_original_recipients()
    {
        var reply = ReplyBuilder.Build(
            Parent(from: "me@example.com", to: "alice@example.com"), "me@example.com", replyAll: false);

        Assert.Contains("alice@example.com", reply.To[0]);
    }

    [Fact]
    public void Quote_carries_an_attribution_and_prefixes_every_line()
    {
        var quoted = ReplyBuilder.Quote(Parent());

        Assert.StartsWith("On ", quoted);
        Assert.Contains("wrote:", quoted);
        Assert.Contains("> The numbers are attached.", quoted);
    }
}
