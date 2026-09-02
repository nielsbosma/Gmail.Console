using System.ComponentModel;
using Gmail.Console.Infrastructure;
using Gmail.Console.Mail;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Gmail.Console.Commands.Draft;

public sealed class GetCommand : MailboxCommand<GetCommand.Settings>
{
    public sealed class Settings : AccountSettings
    {
        [CommandArgument(0, "<DRAFT-ID>")]
        public string DraftId { get; set; } = "";

        [CommandOption("--body <MODE>")]
        [Description("markdown, text, html or none")]
        [DefaultValue("markdown")]
        public string Body { get; set; } = "markdown";

        [CommandOption("--max-chars <N>")]
        [DefaultValue(20000)]
        public int MaxChars { get; set; } = 20000;

        public override ValidationResult Validate()
        {
            if (!BodyModes.IsValid(Body))
                return ValidationResult.Error($"--body must be one of: {string.Join(", ", BodyModes.Names)}");
            return base.Validate();
        }
    }

    protected override async Task<object?> RunAsync(GmailApiClient client, Settings settings, CancellationToken ct)
    {
        using var doc = await client.GetAsync($"drafts/{settings.DraftId}?format=raw", ct);
        var root = doc.RootElement;

        if (!root.TryGetProperty("message", out var envelope))
            throw GmailException.NotFound($"Draft '{settings.DraftId}' has no message.");

        var raw = envelope.TryGetProperty("raw", out var value) ? value.GetString() : null;
        if (string.IsNullOrEmpty(raw))
            throw GmailException.NotFound($"Draft '{settings.DraftId}' returned no content.");

        var mime = MimeBuilder.FromRaw(raw);

        // A draft we wrote is not untrusted third-party content, so it is rendered without the
        // quote-trimming and delimiters that inbound mail gets.
        var body = BodyModes.Parse(settings.Body) switch
        {
            BodyMode.None => null,
            BodyMode.Html => mime.HtmlBody,
            _ => mime.TextBody ?? MessageRenderer.HtmlToMarkdown(mime.HtmlBody ?? "")
        };

        var result = new Dictionary<string, object?>
        {
            ["account"] = client.Account.Name,
            ["draftId"] = settings.DraftId,
            ["messageId"] = envelope.TryGetProperty("id", out var m) ? m.GetString() : null,
            ["threadId"] = envelope.TryGetProperty("threadId", out var t) ? t.GetString() : null,
            ["to"] = mime.To.Select(a => a.ToString()).ToList(),
            ["subject"] = mime.Subject ?? "",
            ["webUrl"] = $"https://mail.google.com/mail/u/0/#drafts?compose={settings.DraftId}",
            ["status"] = "draft_not_sent"
        };

        if (mime.Cc.Count > 0) result["cc"] = mime.Cc.Select(a => a.ToString()).ToList();
        if (mime.Bcc.Count > 0) result["bcc"] = mime.Bcc.Select(a => a.ToString()).ToList();

        if (body is not null)
        {
            var (text, omitted, total) = MessageRenderer.Truncate(MessageRenderer.Normalize(body), settings.MaxChars);
            if (omitted > 0)
            {
                text += $"\n\n[truncated: {omitted:N0} of {total:N0} characters omitted]";
                result["truncated"] = true;
            }
            result["body"] = text;
        }

        return result;
    }
}
