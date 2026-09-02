using System.ComponentModel;
using System.Text.Json;
using Gmail.Console.Infrastructure;
using Gmail.Console.Mail;
using Spectre.Console.Cli;

namespace Gmail.Console.Commands.Draft;

public sealed class ListCommand : MailboxCommand<ListCommand.Settings>
{
    public sealed class Settings : AccountSettings
    {
        [CommandOption("--limit <N>")]
        [DefaultValue(20)]
        public int Limit { get; set; } = 20;

        [CommandOption("--page-token <TOKEN>")]
        public string? PageToken { get; set; }
    }

    protected override async Task<object?> RunAsync(GmailApiClient client, Settings settings, CancellationToken ct)
    {
        var url = "drafts?maxResults=" + settings.Limit;
        if (!string.IsNullOrEmpty(settings.PageToken)) url += "&pageToken=" + Uri.EscapeDataString(settings.PageToken);

        using var listing = await client.GetAsync(url, ct);
        var root = listing.RootElement;

        var drafts = new List<object>();
        if (root.TryGetProperty("drafts", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var draft in list.EnumerateArray())
            {
                var draftId = draft.GetProperty("id").GetString()!;
                var messageId = draft.TryGetProperty("message", out var m) && m.TryGetProperty("id", out var mid)
                    ? mid.GetString()
                    : null;

                var entry = new Dictionary<string, object?>
                {
                    ["draftId"] = draftId,
                    ["messageId"] = messageId,
                    ["webUrl"] = $"https://mail.google.com/mail/u/0/#drafts?compose={draftId}"
                };

                if (messageId is not null)
                {
                    // drafts.list returns ids only, so headers need one metadata fetch each.
                    using var doc = await client.GetAsync($"messages/{messageId}?{GmailFields.MetadataQuery()}", ct);
                    var headers = MessageRenderer.Headers(doc.RootElement);
                    entry["to"] = MessageRenderer.SplitAddresses(headers.GetValueOrDefault("to"));
                    entry["subject"] = headers.GetValueOrDefault("subject") ?? "(no subject)";
                    entry["updated"] = MessageRenderer.InternalDate(doc.RootElement);
                }

                drafts.Add(entry);
            }
        }

        var result = new Dictionary<string, object?>
        {
            ["account"] = client.Account.Name,
            ["count"] = drafts.Count,
            ["drafts"] = drafts
        };

        if (root.TryGetProperty("nextPageToken", out var next) && next.GetString() is { Length: > 0 } token)
            result["nextPageToken"] = token;

        return result;
    }
}
