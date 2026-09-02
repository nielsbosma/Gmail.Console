using System.ComponentModel;
using Gmail.Console.Auth;
using Gmail.Console.Infrastructure;
using Gmail.Console.Mail;
using Spectre.Console.Cli;

namespace Gmail.Console.Commands.Draft;

public sealed class CreateCommand : MailboxCommand<CreateCommand.Settings>
{
    public sealed class Settings : DraftContentSettings
    {
        [CommandOption("--to <ADDRESS>")]
        [Description("Recipient (repeatable, or comma-separated)")]
        public string[] To { get; set; } = [];

        [CommandOption("--cc <ADDRESS>")] public string[] Cc { get; set; } = [];
        [CommandOption("--bcc <ADDRESS>")] public string[] Bcc { get; set; } = [];

        [CommandOption("--subject <SUBJECT>")]
        public string Subject { get; set; } = "";

        [CommandOption("--replace-draft <DRAFT-ID>")]
        [Description("Overwrite an existing draft instead of creating a new one")]
        public string? ReplaceDraft { get; set; }
    }

    protected override async Task<object?> RunAsync(GmailApiClient client, Settings settings, CancellationToken ct)
    {
        ScopeProfiles.RequireDraft(client.Account.Name, client.Account.ScopeProfile);

        var message = MimeBuilder.Build(new DraftContent
        {
            FromEmail = client.Account.Email,
            To = [.. settings.To],
            Cc = [.. settings.Cc],
            Bcc = [.. settings.Bcc],
            Subject = settings.Subject,
            Body = BodyInput.Resolve(settings.BodyPath, settings.BodyText),
            BodyFormat = settings.BodyFormat,
            AttachmentPaths = [.. settings.Attach]
        });

        var raw = MimeBuilder.ToRaw(message);

        using var response = settings.ReplaceDraft is null
            ? await client.PostAsync("drafts", DraftResult.Body(raw, ""), ct)
            : await client.PutAsync($"drafts/{settings.ReplaceDraft}", DraftResult.Body(raw, ""), ct);

        return DraftResult.Describe(
            response.RootElement, message, client.Account.Name,
            settings.ReplaceDraft is null ? "draft_created_not_sent" : "draft_updated_not_sent");
    }
}
