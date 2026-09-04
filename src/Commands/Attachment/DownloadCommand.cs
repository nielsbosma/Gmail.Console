using System.ComponentModel;
using Gmail.Console.Commands.Message;
using Gmail.Console.Infrastructure;
using Gmail.Console.Mail;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Gmail.Console.Commands.Attachment;

public sealed class DownloadCommand : MailboxCommand<DownloadCommand.Settings>
{
    public sealed class Settings : AccountSettings
    {
        [CommandArgument(0, "<MESSAGE-ID>")]
        public string MessageId { get; set; } = "";

        [CommandOption("--all")]
        [Description("Download every attachment on the message")]
        public bool All { get; set; }

        [CommandOption("--attachment-id <ID>")]
        [Description("Download one specific attachment part")]
        public string? AttachmentId { get; set; }

        [CommandOption("--name <PATTERN>")]
        [Description("Download attachments whose filename contains this text")]
        public string? Name { get; set; }

        [CommandOption("--out-dir <DIR>")]
        [Description("Directory to write into")]
        [DefaultValue(".")]
        public string OutDir { get; set; } = ".";

        [CommandOption("--overwrite")]
        [Description("Replace existing files instead of adding a (2) suffix")]
        public bool Overwrite { get; set; }

        [CommandOption("--max-size <BYTES>")]
        [Description("Skip attachments larger than this")]
        [DefaultValue(26214400L)]
        public long MaxSize { get; set; } = 26214400;

        [CommandOption("--include-inline")]
        [Description("Include inline images, which --all skips by default")]
        public bool IncludeInline { get; set; }

        public override ValidationResult Validate()
        {
            if (!All && AttachmentId is null && Name is null)
                return ValidationResult.Error("Pass --all, --attachment-id <id> or --name <pattern>.");
            return base.Validate();
        }
    }

    protected override async Task<object?> RunAsync(GmailApiClient client, Settings settings, CancellationToken ct)
    {
        var parts = await AttachmentsCommand.FetchAsync(client, settings.MessageId, ct);
        var wanted = Select(parts, settings);

        Directory.CreateDirectory(settings.OutDir);

        var saved = new List<object>();
        var skipped = new List<object>();
        var index = 0;

        foreach (var part in wanted)
        {
            index++;

            if (part.Size > settings.MaxSize)
            {
                // One oversized attachment should not fail the whole call.
                skipped.Add(new Dictionary<string, object?>
                {
                    ["filename"] = part.Filename,
                    ["size"] = part.Size,
                    ["reason"] = $"larger than --max-size ({settings.MaxSize} bytes)"
                });
                continue;
            }

            if (string.IsNullOrEmpty(part.AttachmentId))
            {
                skipped.Add(new Dictionary<string, object?>
                {
                    ["filename"] = part.Filename,
                    ["reason"] = "the part carries no attachment id"
                });
                continue;
            }

            var safeName = AttachmentWriter.Sanitize(part.Filename, index);
            var path = AttachmentWriter.ResolvePath(settings.OutDir, safeName, settings.Overwrite);

            var bytes = await client.GetAttachmentAsync(settings.MessageId, part.AttachmentId, ct);
            await File.WriteAllBytesAsync(path, bytes, ct);

            saved.Add(new Dictionary<string, object?>
            {
                ["filename"] = part.Filename,
                ["path"] = path,
                ["mimeType"] = part.MimeType,
                ["size"] = bytes.LongLength
            });
        }

        var result = new Dictionary<string, object?>
        {
            ["account"] = client.Account.Name,
            ["messageId"] = settings.MessageId,
            ["outDir"] = Path.GetFullPath(settings.OutDir),
            ["savedCount"] = saved.Count,
            ["saved"] = saved
        };

        if (skipped.Count > 0) result["skipped"] = skipped;
        if (saved.Count == 0 && skipped.Count == 0) result["note"] = "No attachments matched.";

        return result;
    }

    private static List<AttachmentPart> Select(List<AttachmentPart> parts, Settings settings)
    {
        if (settings.AttachmentId is not null)
        {
            var one = parts.FirstOrDefault(p => p.AttachmentId == settings.AttachmentId)
                ?? throw GmailException.NotFound($"No attachment with id '{settings.AttachmentId}' on this message.");
            return [one];
        }

        var candidates = parts.AsEnumerable();

        if (settings.Name is not null)
            candidates = candidates.Where(p =>
                p.Filename.Contains(settings.Name, StringComparison.OrdinalIgnoreCase));
        else if (!settings.IncludeInline)
            // Inline images are part of the message's presentation, not things someone sent you.
            candidates = candidates.Where(p => !p.Inline);

        return candidates.ToList();
    }
}
