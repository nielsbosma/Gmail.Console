using System.ComponentModel;
using System.Text.Json;
using Gmail.Console.Infrastructure;
using Gmail.Console.Mail;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Gmail.Console.Commands.Thread;

public sealed class GetCommand : MailboxCommand<GetCommand.Settings>
{
    public sealed class Settings : AccountSettings
    {
        [CommandArgument(0, "<THREAD-ID>")]
        public string ThreadId { get; set; } = "";

        [CommandOption("--body <MODE>")]
        [Description("markdown, text, html, none or snippet")]
        [DefaultValue("markdown")]
        public string Body { get; set; } = "markdown";

        [CommandOption("--max-chars <N>")]
        [Description("Truncate each message body at this many characters (0 = no limit)")]
        [DefaultValue(8000)]
        public int MaxChars { get; set; } = 8000;

        [CommandOption("--max-messages <N>")]
        [Description("Return at most this many messages, most recent last")]
        [DefaultValue(20)]
        public int MaxMessages { get; set; } = 20;

        [CommandOption("--keep-quotes")]
        public bool KeepQuotes { get; set; }

        public override ValidationResult Validate()
        {
            if (!BodyModes.IsValid(Body))
                return ValidationResult.Error($"--body must be one of: {string.Join(", ", BodyModes.Names)}");
            if (MaxMessages < 1)
                return ValidationResult.Error("--max-messages must be at least 1.");
            return base.Validate();
        }
    }

    protected override async Task<object?> RunAsync(GmailApiClient client, Settings settings, CancellationToken ct)
    {
        var mode = BodyModes.Parse(settings.Body);

        using var doc = await client.GetAsync($"threads/{settings.ThreadId}?format=raw", ct);
        var root = doc.RootElement;

        if (!root.TryGetProperty("messages", out var messages) || messages.ValueKind != JsonValueKind.Array)
            throw GmailException.NotFound($"Thread '{settings.ThreadId}' has no messages.");

        var all = messages.EnumerateArray().ToList();
        var total = all.Count;

        // Keep the most recent: in a long thread the tail is what a reply needs.
        var selected = all.Count > settings.MaxMessages ? all[^settings.MaxMessages..] : all;

        var rendered = new List<object>();
        foreach (var envelope in selected)
        {
            var raw = envelope.TryGetProperty("raw", out var value) ? value.GetString() : null;
            if (string.IsNullOrEmpty(raw)) continue;

            var mime = MimeBuilder.FromRaw(raw);
            rendered.Add(MessageRenderer.Full(
                envelope, mime, mode, settings.MaxChars, settings.KeepQuotes, allHeaders: false));
        }

        var result = new Dictionary<string, object?>
        {
            ["account"] = client.Account.Name,
            ["threadId"] = settings.ThreadId,
            ["messageCount"] = total,
            ["returned"] = rendered.Count,
            ["messages"] = rendered
        };

        if (rendered.Count < total)
            result["note"] = $"Showing the {rendered.Count} most recent of {total} messages. Raise --max-messages for more.";

        return result;
    }
}
