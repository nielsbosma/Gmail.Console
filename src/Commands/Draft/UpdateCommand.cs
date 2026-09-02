using System.ComponentModel;
using Gmail.Console.Auth;
using Gmail.Console.Infrastructure;
using Gmail.Console.Mail;
using Spectre.Console.Cli;

namespace Gmail.Console.Commands.Draft;

/// <summary>
/// Replaces a draft's content wholesale. This is the revise step of the write / read back /
/// revise loop, and the reason an agent never needs to create a second draft (spec G12).
/// </summary>
public sealed class UpdateCommand : MailboxCommand<UpdateCommand.Settings>
{
    public sealed class Settings : DraftContentSettings
    {
        [CommandArgument(0, "<DRAFT-ID>")]
        public string DraftId { get; set; } = "";

        [CommandOption("--to <ADDRESS>")] public string[] To { get; set; } = [];
        [CommandOption("--cc <ADDRESS>")] public string[] Cc { get; set; } = [];
        [CommandOption("--bcc <ADDRESS>")] public string[] Bcc { get; set; } = [];

        [CommandOption("--subject <SUBJECT>")] public string? Subject { get; set; }
    }

    protected override async Task<object?> RunAsync(GmailApiClient client, Settings settings, CancellationToken ct)
    {
        ScopeProfiles.RequireDraft(client.Account.Name, client.Account.ScopeProfile);

        // Read the existing draft first so unspecified fields — recipients, subject, the thread
        // it belongs to — survive an update that only changes the body.
        using var existing = await client.GetAsync($"drafts/{settings.DraftId}?format=raw", ct);
        var envelope = existing.RootElement.GetProperty("message");

        var rawExisting = envelope.TryGetProperty("raw", out var value) ? value.GetString() : null;
        if (string.IsNullOrEmpty(rawExisting))
            throw GmailException.NotFound($"Draft '{settings.DraftId}' returned no content.");

        var current = MimeBuilder.FromRaw(rawExisting);
        var threadId = envelope.TryGetProperty("threadId", out var t) ? t.GetString() ?? "" : "";

        var body = settings.BodyPath is null && settings.BodyText is null
            ? current.TextBody ?? MessageRenderer.HtmlToMarkdown(current.HtmlBody ?? "")
            : BodyInput.Resolve(settings.BodyPath, settings.BodyText);

        var message = MimeBuilder.Build(new DraftContent
        {
            FromEmail = client.Account.Email,
            To = settings.To.Length > 0 ? [.. settings.To] : current.To.Select(a => a.ToString()).ToList(),
            Cc = settings.Cc.Length > 0 ? [.. settings.Cc] : current.Cc.Select(a => a.ToString()).ToList(),
            Bcc = settings.Bcc.Length > 0 ? [.. settings.Bcc] : current.Bcc.Select(a => a.ToString()).ToList(),
            Subject = settings.Subject ?? current.Subject ?? "",
            Body = body,
            BodyFormat = settings.BodyFormat,
            AttachmentPaths = [.. settings.Attach],
            InReplyTo = current.InReplyTo,
            References = current.References.ToList()
        });

        var raw = MimeBuilder.ToRaw(message);
        using var response = await client.PutAsync($"drafts/{settings.DraftId}", DraftResult.Body(raw, threadId), ct);

        return DraftResult.Describe(response.RootElement, message, client.Account.Name, "draft_updated_not_sent");
    }
}
