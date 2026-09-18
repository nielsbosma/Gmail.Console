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

        [CommandOption("--ids-only")]
        [Description("Return ids and thread ids straight from the listing, without fetching each message")]
        public bool IdsOnly { get; set; }

        [CommandOption("--all")]
        [Description("Follow nextPageToken until the query is exhausted or --max is reached")]
        public bool All { get; set; }

        [CommandOption("--max <N>")]
        [Description("With --all, stop after this many messages")]
        [DefaultValue(1000)]
        public int Max { get; set; } = 1000;

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
            if (Max < 1)
                return ValidationResult.Error("--max must be at least 1.");

            // Without --ids-only every page costs a fetch per message, so an unattended
            // --all over a large mailbox is a guaranteed trip through the rate limiter.
            if (All && !IdsOnly && Max > 500)
                return ValidationResult.Error(
                    "--all without --ids-only fetches every message individually. "
                    + "Add --ids-only, or set --max to 500 or less.");

            return base.Validate();
        }
    }

    protected override async Task<object?> RunAsync(GmailApiClient client, Settings settings, CancellationToken ct)
    {
        var query = BuildQuery(settings);
        var limit = settings.All ? Math.Min(settings.Limit, settings.Max) : settings.Limit;

        var ids = new List<(string Id, string ThreadId)>();
        var seenThreads = new HashSet<string>(StringComparer.Ordinal);
        var token = settings.PageToken;
        string? nextToken = null;
        long? estimate = null;
        var pages = 0;

        do
        {
            using var listing = await client.GetAsync(ListPath(query, limit, token, settings), ct);
            var root = listing.RootElement;
            pages++;

            if (root.TryGetProperty("messages", out var messages) && messages.ValueKind == JsonValueKind.Array)
            {
                foreach (var message in messages.EnumerateArray())
                {
                    var id = message.GetProperty("id").GetString()!;
                    var threadId = message.TryGetProperty("threadId", out var t) ? t.GetString() ?? id : id;

                    if (settings.GroupThreads && !seenThreads.Add(threadId)) continue;
                    ids.Add((id, threadId));
                    if (settings.All && ids.Count >= settings.Max) break;
                }
            }

            if (estimate is null &&
                root.TryGetProperty("resultSizeEstimate", out var size) && size.TryGetInt64(out var value))
                estimate = value;

            nextToken = root.TryGetProperty("nextPageToken", out var next) ? next.GetString() : null;
            token = nextToken;
        }
        while (settings.All && !string.IsNullOrEmpty(token) && ids.Count < settings.Max && !ct.IsCancellationRequested);

        // --ids-only stops here, at one API call per page. That is the difference between
        // enumerating a mailbox and exhausting the per-minute quota doing it: hydration below
        // costs a messages.get for every single result.
        var rows = settings.IdsOnly
            ? ids.Select(x => (object)new Dictionary<string, object?> { ["id"] = x.Id, ["threadId"] = x.ThreadId }).ToList()
            : await HydrateAsync(client, ids, settings.Concurrency, ct);

        var failed = rows.Count(r => r is Dictionary<string, object?> d && d.ContainsKey("error"));

        var result = new Dictionary<string, object?>
        {
            ["query"] = query,
            ["account"] = client.Account.Name,
            ["count"] = rows.Count,
            ["messages"] = rows
        };

        if (settings.All) result["pages"] = pages;

        // A caller iterating `messages` would otherwise walk straight into entries that carry
        // no headers, having seen a `count` that says they are all there.
        if (failed > 0)
        {
            result["failed"] = failed;
            result["note"] = $"{failed} of {rows.Count} messages could not be fetched and carry "
                           + "an `error` field instead of headers. Retry, or use --ids-only.";
        }

        if (!string.IsNullOrEmpty(nextToken)) result["nextPageToken"] = nextToken;
        if (estimate is not null) result["resultSizeEstimate"] = estimate;

        return result;
    }

    public static string ListPath(string query, int limit, string? pageToken, Settings settings)
    {
        var url = new StringBuilder("messages?maxResults=" + limit);
        if (query.Length > 0) url.Append("&q=").Append(Uri.EscapeDataString(query));
        if (!string.IsNullOrEmpty(pageToken)) url.Append("&pageToken=").Append(Uri.EscapeDataString(pageToken));
        if (settings.IncludeSpamTrash) url.Append("&includeSpamTrash=true");
        foreach (var label in settings.Labels) url.Append("&labelIds=").Append(Uri.EscapeDataString(label));
        return url.ToString();
    }

    /// <summary>
    /// messages.list returns bare ids, so every useful search is an N-way fan-out behind it.
    /// Bounded concurrency keeps a 50-result search inside the 250 units/second per-user budget,
    /// and one failed fetch degrades that entry instead of the whole search. See spec G07.
    /// </summary>
    private static async Task<List<object>> HydrateAsync(
        GmailApiClient client, List<(string Id, string ThreadId)> ids, int concurrency, CancellationToken ct)
    {
        var results = new object?[ids.Count];
        using var gate = new SemaphoreSlim(concurrency);

        var tasks = ids.Select(async ((string Id, string ThreadId) entry, int index) =>
        {
            await gate.WaitAsync(ct);
            try
            {
                using var doc = await client.GetAsync($"messages/{entry.Id}?format=full&fields={Uri.EscapeDataString(GmailFields.Structure)}", ct);
                results[index] = MessageRenderer.Summary(doc.RootElement);
            }
            catch (GmailException ex)
            {
                results[index] = new Dictionary<string, object?>
                {
                    ["id"] = entry.Id,
                    // The listing already told us this, and a caller grouping by thread should
                    // not lose the row just because its headers did not arrive.
                    ["threadId"] = entry.ThreadId,
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
