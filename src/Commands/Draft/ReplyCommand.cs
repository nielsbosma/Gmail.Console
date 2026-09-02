using System.ComponentModel;
using Gmail.Console.Auth;
using Gmail.Console.Infrastructure;
using Gmail.Console.Mail;
using Spectre.Console.Cli;

namespace Gmail.Console.Commands.Draft;

/// <summary>
/// Given a message id and a body, derives everything else from the parent: threadId,
/// In-Reply-To, References, the Re: prefix and the recipient set. See spec section 10.
/// </summary>
public sealed class ReplyCommand : MailboxCommand<ReplyCommand.Settings>
{
    public sealed class Settings : DraftContentSettings
    {
        [CommandArgument(0, "<MESSAGE-ID>")]
        [Description("The message being replied to")]
        public string MessageId { get; set; } = "";

        [CommandOption("--all")]
        [Description("Reply to everyone: the parent's To and Cc, minus your own address")]
        public bool All { get; set; }

        [CommandOption("--no-quote")]
        [Description("Do not append the quoted parent message")]
        public bool NoQuote { get; set; }

        [CommandOption("--to <ADDRESS>")]
        [Description("Override the derived recipients")]
        public string[] To { get; set; } = [];

        [CommandOption("--cc <ADDRESS>")]
        [Description("Add to the derived Cc list")]
        public string[] Cc { get; set; } = [];
    }

    protected override async Task<object?> RunAsync(GmailApiClient client, Settings settings, CancellationToken ct)
    {
        ScopeProfiles.RequireDraft(client.Account.Name, client.Account.ScopeProfile);

        using var doc = await client.GetAsync($"messages/{settings.MessageId}?format=raw", ct);
        var envelope = doc.RootElement;

        var rawParent = envelope.TryGetProperty("raw", out var value) ? value.GetString() : null;
        if (string.IsNullOrEmpty(rawParent))
            throw GmailException.NotFound($"Message '{settings.MessageId}' returned no content.");

        var parent = MimeBuilder.FromRaw(rawParent);
        var threadId = envelope.TryGetProperty("threadId", out var t) ? t.GetString() ?? "" : "";

        var reply = ReplyBuilder.Build(parent, client.Account.Email, settings.All);

        var body = BodyInput.Resolve(settings.BodyPath, settings.BodyText);
        if (!settings.NoQuote)
            body = body.TrimEnd() + "\n\n" + reply.QuotedParent;

        var message = MimeBuilder.Build(new DraftContent
        {
            FromEmail = client.Account.Email,
            To = settings.To.Length > 0 ? [.. settings.To] : reply.To,
            Cc = [.. reply.Cc, .. settings.Cc],
            Subject = reply.Subject,
            Body = body,
            BodyFormat = settings.BodyFormat,
            AttachmentPaths = [.. settings.Attach],
            InReplyTo = reply.InReplyTo,
            References = reply.References
        });

        var raw = MimeBuilder.ToRaw(message);

        // threadId is what keeps Gmail's own UI showing one conversation; the In-Reply-To and
        // References headers are what every other mail client threads on. Both are required.
        using var response = await client.PostAsync("drafts", DraftResult.Body(raw, threadId), ct);

        var result = DraftResult.Describe(response.RootElement, message, client.Account.Name, "draft_created_not_sent");
        result["inReplyTo"] = reply.InReplyTo is null ? null : "<" + reply.InReplyTo + ">";
        result["replyAll"] = settings.All;
        return result;
    }
}
