using System.Text;
using System.Text.RegularExpressions;
using MimeKit;

namespace Gmail.Console.Mail;

/// <summary>
/// A reply is not "a draft with Re: in the subject". Getting the threading headers and the
/// recipient set right is the whole point of having a command for it — an agent asked to build
/// this itself will fork the thread in the recipient's client. See spec section 10.
/// </summary>
public static partial class ReplyBuilder
{
    public sealed record Reply(
        List<string> To,
        List<string> Cc,
        string Subject,
        string? InReplyTo,
        List<string> References,
        string QuotedParent);

    public static Reply Build(MimeMessage parent, string ownEmail, bool replyAll)
    {
        // Reply-To wins over From when the sender asked for it.
        var to = parent.ReplyTo.Count > 0
            ? parent.ReplyTo.Mailboxes.ToList()
            : parent.From.Mailboxes.ToList();

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ownEmail };
        var toAddresses = new List<string>();
        foreach (var mailbox in to)
        {
            if (seen.Add(mailbox.Address)) toAddresses.Add(mailbox.ToString());
        }

        // Replying to your own message: keep the original recipients rather than producing a
        // draft addressed to nobody.
        if (toAddresses.Count == 0)
            toAddresses = parent.To.Mailboxes.Select(m => m.ToString()).ToList();

        var ccAddresses = new List<string>();
        if (replyAll)
        {
            foreach (var mailbox in parent.To.Mailboxes.Concat(parent.Cc.Mailboxes))
            {
                if (seen.Add(mailbox.Address)) ccAddresses.Add(mailbox.ToString());
            }
        }

        var references = parent.References.ToList();
        if (!string.IsNullOrEmpty(parent.MessageId) && !references.Contains(parent.MessageId))
            references.Add(parent.MessageId);

        return new Reply(
            toAddresses,
            ccAddresses,
            Subject(parent.Subject),
            parent.MessageId,
            references,
            Quote(parent));
    }

    /// <summary>
    /// One "Re: " prefix, never "Re: Re:". Localized prefixes already on the subject
    /// (SV:, AW:, VS:) count as existing ones and are replaced rather than stacked.
    /// </summary>
    public static string Subject(string? parentSubject)
    {
        var subject = (parentSubject ?? "").Trim();

        while (true)
        {
            var match = ReplyPrefix().Match(subject);
            if (!match.Success) break;
            subject = subject[match.Length..].TrimStart();
        }

        return "Re: " + subject;
    }

    public static string Quote(MimeMessage parent)
    {
        var sender = parent.From.Count > 0 ? parent.From.ToString() : "someone";
        var when = parent.Date == default
            ? "an earlier message"
            : parent.Date.UtcDateTime.ToString("ddd, d MMM yyyy 'at' HH:mm 'UTC'");

        var source = parent.TextBody ?? MessageRenderer.HtmlToMarkdown(parent.HtmlBody ?? "");
        source = MessageRenderer.Normalize(MessageRenderer.TrimQuotedReply(source));

        var quoted = new StringBuilder();
        quoted.Append("On ").Append(when).Append(", ").Append(sender).Append(" wrote:").Append('\n');
        foreach (var line in source.Split('\n'))
            quoted.Append(line.Length == 0 ? ">" : "> " + line).Append('\n');

        return quoted.ToString().TrimEnd();
    }

    [GeneratedRegex(@"^(re|sv|aw|antw|vs|ref|fwd|vb)\s*(\[\d+\])?\s*:\s*", RegexOptions.IgnoreCase)]
    private static partial Regex ReplyPrefix();
}
