using Gmail.Console.Infrastructure;
using Gmail.Console.Mail;
using Spectre.Console.Cli;

namespace Gmail.Console.Commands.Message;

/// <summary>Lists the attachment parts without transferring any of them.</summary>
public sealed class AttachmentsCommand : MailboxCommand<AttachmentsCommand.Settings>
{
    public sealed class Settings : AccountSettings
    {
        [CommandArgument(0, "<MESSAGE-ID>")]
        public string MessageId { get; set; } = "";
    }

    protected override async Task<object?> RunAsync(GmailApiClient client, Settings settings, CancellationToken ct)
    {
        var parts = await FetchAsync(client, settings.MessageId, ct);

        return new Dictionary<string, object?>
        {
            ["account"] = client.Account.Name,
            ["messageId"] = settings.MessageId,
            ["count"] = parts.Count,
            ["attachments"] = parts.Select(p => (object)new Dictionary<string, object?>
            {
                ["attachmentId"] = p.AttachmentId,
                ["filename"] = p.Filename,
                ["safeFilename"] = AttachmentWriter.Sanitize(p.Filename, 1),
                ["mimeType"] = p.MimeType,
                ["size"] = p.Size,
                ["inline"] = p.Inline
            }).ToList()
        };
    }

    public static async Task<List<AttachmentPart>> FetchAsync(
        GmailApiClient client, string messageId, CancellationToken ct)
    {
        using var doc = await client.GetAsync(
            $"messages/{messageId}?format=full&fields={Uri.EscapeDataString(GmailFields.Structure)}", ct);

        return MessageRenderer.DescribeAttachments(doc.RootElement);
    }
}
