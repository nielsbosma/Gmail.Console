using System.ComponentModel;
using System.Text;
using System.Text.Json;
using Gmail.Console.Infrastructure;
using Gmail.Console.Mail;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Gmail.Console.Commands;

public sealed class SearchCommand : MailboxCommand<SearchCommand.Settings>
{
    public sealed class Settings : AccountSettings
    {
        [CommandArgument(0, "[QUERY]")]
        [Description("Gmail search query, e.g. \"from:stripe.com has:attachment newer_than:30d\"")]
        public string? Query { get; set; }

        [CommandOption("--limit <N>")]
        [Description("Maximum messages to return")]
        [DefaultValue(20)]
        public int Limit { get; set; } = 20;

        [CommandOption("--page-token <TOKEN>")]
        [Description("Continue from a previous result's nextPageToken")]
        public string? PageToken { get; set; }

        [CommandOption("--label <LABEL>")]
        [Description("Restrict to a label id, e.g. INBOX (repeatable)")]
        public string[] Labels { get; set; } = [];

        [CommandOption("--from <ADDRESS>")] public string? From { get; set; }
        [CommandOption("--to <ADDRESS>")] public string? To { get; set; }
        [CommandOption("--subject <TEXT>")] public string? Subject { get; set; }

        [CommandOption("--unread")]
        [Description("Only unread messages")]
        public bool Unread { get; set; }

        [CommandOption("--has-attachment")]
        [Description("Only messages with attachments")]
        public bool HasAttachment { get; set; }

        [CommandOption("--after <DATE>")]
        [Description("Messages after this date (YYYY-MM-DD)")]
        public string? After { get; set; }

        [CommandOption("--before <DATE>")]
        [Description("Messages before this date (YYYY-MM-DD)")]
        public string? Before { get; set; }

        [CommandOption("--include-spam-trash")]
        public bool IncludeSpamTrash { get; set; }

        [CommandOption("--group-threads")]
        [Description("Collapse to one entry per thread")]
        public bool GroupThreads { get; set; }

        [CommandOption("--concurrency <N>")]
        [Description("Parallel message fetches")]
        [DefaultValue(5)]
        public int Concurrency { get; set; } = 5;

        public override ValidationResult Validate()
        {
            if (Limit is < 1 or > 500)
                return ValidationResult.Error("--limit must be between 1 and 500.");
            if (Concurrency is < 1 or > 20)
                return ValidationResult.Error("--concurrency must be between 1 and 20.");
            return base.Validate();
        }
    }

    protected override async Task<object?> RunAsync(GmailApiClient client, Settings settings, CancellationToken ct)
    {
        var query = BuildQuery(settings);

        var url = new StringBuilder("messages?maxResults=" + settings.Limit);
        if (query.Length > 0) url.Append("&q=").Append(Uri.EscapeDataString(query));
        if (!string.IsNullOrEmpty(settings.PageToken)) url.Append("&pageToken=").Append(Uri.EscapeDataString(settings.PageToken));
        if (settings.IncludeSpamTrash) url.Append("&includeSpamTrash=true");
        foreach (var label in settings.Labels) url.Append("&labelIds=").Append(Uri.EscapeDataString(label));

        using var listing = await client.GetAsync(url.ToString(), ct);
        var root = listing.RootElement;

        var ids = new List<(string Id, string ThreadId)>();
        if (root.TryGetProperty("messages", out var messages) && messages.ValueKind == JsonValueKind.Array)
        {
            foreach (var message in messages.EnumerateArray())
            {
                var id = message.GetProperty("id").GetString()!;
                var threadId = message.TryGetProperty("threadId", out var t) ? t.GetString() ?? id : id;

                if (settings.GroupThreads && ids.Any(x => x.ThreadId == threadId)) continue;
                ids.Add((id, threadId));
            }
        }

        var hydrated = await HydrateAsync(client, ids.Select(x => x.Id).ToList(), settings.Concurrency, ct);

        var result = new Dictionary<string, object?>
        {
            ["query"] = query,
            ["account"] = client.Account.Name,
            ["count"] = hydrated.Count,
            ["messages"] = hydrated
        };

        if (root.TryGetProperty("nextPageToken", out var next) && next.GetString() is { Length: > 0 } token)
            result["nextPageToken"] = token;

        if (root.TryGetProperty("resultSizeEstimate", out var estimate) && estimate.TryGetInt64(out var size))
            result["resultSizeEstimate"] = size;

        return result;
    }

    /// <summary>
    /// messages.list returns bare ids, so every useful search is an N-way fan-out behind it.
    /// Bounded concurrency keeps a 50-result search inside the 250 units/second per-user budget,
    /// and one failed fetch degrades that entry instead of the whole search. See spec G07.
    /// </summary>
    private static async Task<List<object>> HydrateAsync(
        GmailApiClient client, List<string> ids, int concurrency, CancellationToken ct)
    {
        var results = new object?[ids.Count];
        using var gate = new SemaphoreSlim(concurrency);

        var tasks = ids.Select(async (id, index) =>
        {
            await gate.WaitAsync(ct);
            try
            {
                using var doc = await client.GetAsync($"messages/{id}?format=full&fields={Uri.EscapeDataString(GmailFields.Structure)}", ct);
                results[index] = MessageRenderer.Summary(doc.RootElement);
            }
            catch (GmailException ex)
            {
                results[index] = new Dictionary<string, object?>
                {
                    ["id"] = id,
                    ["error"] = "fetch_failed",
                    ["detail"] = ex.Message
                };
            }
            finally
            {
                gate.Release();
            }
        });

        await Task.WhenAll(tasks);
        return results.Where(r => r is not null).Select(r => r!).ToList();
    }

    private static string BuildQuery(Settings settings)
    {
        var parts = new List<string>();

        if (!string.IsNullOrWhiteSpace(settings.Query)) parts.Add(settings.Query.Trim());
        if (!string.IsNullOrWhiteSpace(settings.From)) parts.Add("from:" + Quote(settings.From));
        if (!string.IsNullOrWhiteSpace(settings.To)) parts.Add("to:" + Quote(settings.To));
        if (!string.IsNullOrWhiteSpace(settings.Subject)) parts.Add("subject:" + Quote(settings.Subject));
        if (settings.Unread) parts.Add("is:unread");
        if (settings.HasAttachment) parts.Add("has:attachment");
        if (!string.IsNullOrWhiteSpace(settings.After)) parts.Add("after:" + settings.After.Replace('-', '/'));
        if (!string.IsNullOrWhiteSpace(settings.Before)) parts.Add("before:" + settings.Before.Replace('-', '/'));

        return string.Join(' ', parts);
    }

    private static string Quote(string value) => value.Contains(' ') ? $"\"{value}\"" : value;
}
