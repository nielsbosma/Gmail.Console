using Gmail.Console.Infrastructure;
using MimeKit;
using MimeKit.Text;

namespace Gmail.Console.Mail;

public sealed class DraftContent
{
    public required string FromEmail { get; init; }
    public List<string> To { get; init; } = [];
    public List<string> Cc { get; init; } = [];
    public List<string> Bcc { get; init; } = [];
    public string Subject { get; init; } = "";
    public string Body { get; init; } = "";

    /// <summary>plain | markdown | html — how <see cref="Body"/> should be interpreted.</summary>
    public string BodyFormat { get; init; } = "markdown";

    public List<string> AttachmentPaths { get; init; } = [];

    /// <summary>Set for replies: the parent's Message-ID, and the accumulated References chain.</summary>
    public string? InReplyTo { get; init; }
    public List<string> References { get; init; } = [];
}

public static class MimeBuilder
{
    public static MimeMessage Build(DraftContent content)
    {
        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(content.FromEmail));

        AddAll(message.To, content.To, "--to");
        AddAll(message.Cc, content.Cc, "--cc");
        AddAll(message.Bcc, content.Bcc, "--bcc");

        if (message.To.Count == 0 && message.Cc.Count == 0 && message.Bcc.Count == 0)
            throw GmailException.Invalid("A draft needs at least one recipient.", "Pass --to <address>.");

        message.Subject = content.Subject;

        if (!string.IsNullOrEmpty(content.InReplyTo))
        {
            message.InReplyTo = content.InReplyTo;
            foreach (var reference in content.References) message.References.Add(reference);
        }

        var builder = new BodyBuilder();

        switch (content.BodyFormat)
        {
            case "html":
                builder.HtmlBody = content.Body;
                builder.TextBody = MessageRenderer.HtmlToMarkdown(content.Body);
                break;

            case "plain":
                builder.TextBody = content.Body;
                break;

            default:
                // multipart/alternative: the markdown source as the plain part, rendered HTML
                // beside it — which is what a human recipient's client will show.
                builder.TextBody = content.Body;
                builder.HtmlBody = Markdig.Markdown.ToHtml(content.Body);
                break;
        }

        foreach (var path in content.AttachmentPaths)
        {
            if (!File.Exists(path))
                throw GmailException.Invalid($"Attachment not found: {path}");
            builder.Attachments.Add(path);
        }

        message.Body = builder.ToMessageBody();
        return message;
    }

    /// <summary>Gmail wants the whole RFC 5322 message base64url-encoded in a JSON field.</summary>
    public static string ToRaw(MimeMessage message)
    {
        using var stream = new MemoryStream();
        message.WriteTo(FormatOptions.Default, stream);
        return GmailApiClient.Base64UrlEncode(stream.ToArray());
    }

    public static MimeMessage FromRaw(string base64Url)
    {
        using var stream = new MemoryStream(GmailApiClient.Base64UrlDecode(base64Url));
        return MimeMessage.Load(stream);
    }

    private static void AddAll(InternetAddressList list, List<string> addresses, string option)
    {
        foreach (var entry in addresses.SelectMany(SplitList))
        {
            if (!MailboxAddress.TryParse(entry, out var address))
                throw GmailException.Invalid($"'{entry}' is not a valid email address ({option}).");
            list.Add(address);
        }
    }

    /// <summary>Accepts both repeated options and a single comma-separated value.</summary>
    private static IEnumerable<string> SplitList(string value) =>
        value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public static TextFormat FormatOf(string bodyFormat) =>
        bodyFormat == "html" ? TextFormat.Html : TextFormat.Plain;
}
