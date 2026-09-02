using System.ComponentModel;
using Gmail.Console.Infrastructure;
using Gmail.Console.Mail;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Gmail.Console.Commands.Message;

public sealed class GetCommand : MailboxCommand<GetCommand.Settings>
{
    public sealed class Settings : AccountSettings
    {
        [CommandArgument(0, "<MESSAGE-ID>")]
        public string MessageId { get; set; } = "";

        [CommandOption("--body <MODE>")]
        [Description("markdown, text, html, none or snippet")]
        [DefaultValue("markdown")]
        public string Body { get; set; } = "markdown";

        [CommandOption("--max-chars <N>")]
        [Description("Truncate the body at this many characters (0 = no limit)")]
        [DefaultValue(20000)]
        public int MaxChars { get; set; } = 20000;

        [CommandOption("--keep-quotes")]
        [Description("Keep the quoted parent message in a reply body")]
        public bool KeepQuotes { get; set; }

        [CommandOption("--headers")]
        [Description("Include every RFC 5322 header")]
        public bool AllHeaders { get; set; }

        [CommandOption("--save-raw <PATH>")]
        [Description("Also write the raw RFC 5322 message to this file")]
        public string? SaveRaw { get; set; }

        public override ValidationResult Validate()
        {
            if (!BodyModes.IsValid(Body))
                return ValidationResult.Error($"--body must be one of: {string.Join(", ", BodyModes.Names)}");
            if (MaxChars < 0)
                return ValidationResult.Error("--max-chars cannot be negative.");
            return base.Validate();
        }
    }

    protected override async Task<object?> RunAsync(GmailApiClient client, Settings settings, CancellationToken ct)
    {
        var mode = BodyModes.Parse(settings.Body);

        // format=raw and let MimeKit parse it: encoded-words, quoted-printable, nested
        // multipart and charset detection are all easy to get subtly wrong by hand.
        using var doc = await client.GetAsync($"messages/{settings.MessageId}?format=raw", ct);
        var envelope = doc.RootElement;

        var raw = envelope.TryGetProperty("raw", out var value) ? value.GetString() : null;
        if (string.IsNullOrEmpty(raw))
            throw GmailException.NotFound($"Message '{settings.MessageId}' returned no content.");

        if (!string.IsNullOrEmpty(settings.SaveRaw))
            await File.WriteAllBytesAsync(settings.SaveRaw, GmailApiClient.Base64UrlDecode(raw), ct);

        var mime = MimeBuilder.FromRaw(raw);

        var result = MessageRenderer.Full(envelope, mime, mode, settings.MaxChars, settings.KeepQuotes, settings.AllHeaders);
        result["account"] = client.Account.Name;

        if (!string.IsNullOrEmpty(settings.SaveRaw))
            result["savedRawTo"] = Path.GetFullPath(settings.SaveRaw);

        return result;
    }
}
