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
        [CommandArgument(0, "[THREAD-ID]")]
        public string ThreadId { get; set; } = "";

        [CommandOption("--ids-from <FILE>")]
        [Description("Fetch every thread id listed in this file, one per line, instead of a single id")]
        public string? IdsFrom { get; set; }

        [CommandOption("--out-dir <DIR>")]
        [Description("With --ids-from: write one document per thread here, named <threadId>.<format>")]
        public string? OutDir { get; set; }

        [CommandOption("--overwrite")]
        [Description("With --out-dir: refetch threads whose file already exists")]
        public bool Overwrite { get; set; }

        [CommandOption("--concurrency <N>")]
        [Description("With --ids-from: threads fetched in parallel. Raising this usually makes a large run slower")]
        [DefaultValue(2)]
        public int Concurrency { get; set; } = 2;

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

            var single = !string.IsNullOrWhiteSpace(ThreadId);
            var bulk = !string.IsNullOrWhiteSpace(IdsFrom);

            if (single == bulk)
                return ValidationResult.Error("Pass either a thread id or --ids-from <FILE>, not both.");

            // A bulk fetch of a few thousand threads would otherwise render every body onto
            // stdout as one document, which is unusable for the corpus work it exists for.
            if (bulk && string.IsNullOrWhiteSpace(OutDir))
                return ValidationResult.Error("--ids-from requires --out-dir.");
            if (bulk && !File.Exists(IdsFrom))
                return ValidationResult.Error($"--ids-from: no such file '{IdsFrom}'.");
            if (Concurrency is < 1 or > 10)
                return ValidationResult.Error("--concurrency must be between 1 and 10.");

            return base.Validate();
        }
    }

    /// <summary>
    /// threads.get accepts full, metadata and minimal — but <em>not</em> raw, unlike messages.get.
    /// minimal is all we need here: the message ids, in order.
    /// </summary>
    public static string ThreadPath(string threadId) => $"threads/{threadId}?format=minimal";

    /// <summary>The per-message fetch, identical to the one <c>message get</c> makes.</summary>
    public static string MessagePath(string messageId) => $"messages/{messageId}?format=raw";

    protected override Task<object?> RunAsync(GmailApiClient client, Settings settings, CancellationToken ct) =>
        string.IsNullOrWhiteSpace(settings.IdsFrom)
            ? FetchOneAsync(client, settings, settings.ThreadId, ct)
            : FetchManyAsync(client, settings, ct);

    /// <summary>
    /// One process, one token refresh and one backoff state for the whole corpus — a shell loop
    /// spawning the tool per thread pays a process start for each and cannot pace itself against
    /// the rate limiter. Resumable by default: a thread whose file is already on disk is skipped,
    /// so an interrupted run continues rather than starting over.
    /// </summary>
    private static async Task<object?> FetchManyAsync(GmailApiClient client, Settings settings, CancellationToken ct)
    {
        var outDir = settings.OutDir!;
        Directory.CreateDirectory(outDir);

        var extension = settings.Format.Equals("json", StringComparison.OrdinalIgnoreCase) ? "json" : "yaml";
        var ids = File.ReadLines(settings.IdsFrom!)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        int written = 0, skipped = 0;
        var errors = new List<object>();
        var errorLock = new object();

        // A thread's own messages are fetched in order, so the only parallelism available is
        // across threads. Two at a time, not more: measured over 30 threads on a real mailbox,
        // concurrency 2 took 19s with 3 retries and concurrency 6 took 119s with 27. The
        // limiter charges far more for a burst than the extra requests are worth, so past a
        // very low ceiling every added worker makes the whole run slower.
        using var gate = new SemaphoreSlim(settings.Concurrency);

        await Task.WhenAll(ids.Select(async id =>
        {
            await gate.WaitAsync(ct);
            try
            {
                var path = Path.Combine(outDir, $"{Sanitize(id)}.{extension}");
                if (!settings.Overwrite && File.Exists(path))
                {
                    Interlocked.Increment(ref skipped);
                    return;
                }

                var thread = await FetchOneAsync(client, settings, id, ct);
                await File.WriteAllTextAsync(path, OutputHelper.Serialize(thread!, settings.Format), ct);

                var done = Interlocked.Increment(ref written);
                if (done % 100 == 0)
                    OutputHelper.Status($"  {done} written, {Volatile.Read(ref skipped)} already present, {errors.Count} failed");
            }
            catch (GmailException ex)
            {
                // One unreadable thread should not end a run of several thousand.
                lock (errorLock)
                {
                    errors.Add(new Dictionary<string, object?>
                    {
                        ["threadId"] = id,
                        ["code"] = ex.Code.ToString(),
                        ["detail"] = ex.Message
                    });
                }
            }
            finally
            {
                gate.Release();
            }
        }));

        var result = new Dictionary<string, object?>
        {
            ["account"] = client.Account.Name,
            ["outDir"] = Path.GetFullPath(outDir),
            ["requested"] = ids.Count,
            ["written"] = written,
            ["skipped"] = skipped,
            ["failed"] = errors.Count
        };
        if (errors.Count > 0) result["errors"] = errors;
        if (skipped > 0 && !settings.Overwrite)
            result["note"] = $"{skipped} threads already had a file and were not refetched. "
                           + "Pass --overwrite to refresh them.";
        return result;
    }

    /// <summary>Thread ids are hex, but the file name is ours to guarantee.</summary>
    private static string Sanitize(string id) =>
        string.Concat(id.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '_'));

    private static async Task<object?> FetchOneAsync(
        GmailApiClient client, Settings settings, string threadId, CancellationToken ct)
    {
        var mode = BodyModes.Parse(settings.Body);

        using var doc = await client.GetAsync(ThreadPath(threadId), ct);
        var root = doc.RootElement;

        if (!root.TryGetProperty("messages", out var messages) || messages.ValueKind != JsonValueKind.Array)
            throw GmailException.NotFound($"Thread '{threadId}' has no messages.");

        var all = messages.EnumerateArray()
            .Select(m => m.TryGetProperty("id", out var id) ? id.GetString() : null)
            .Where(id => !string.IsNullOrEmpty(id))
            .Select(id => id!)
            .ToList();
        var total = all.Count;

        // Keep the most recent: in a long thread the tail is what a reply needs. Select before
        // fetching so a long thread does not pull down more messages than will be shown.
        var selected = all.Count > settings.MaxMessages ? all[^settings.MaxMessages..] : all;

        var rendered = new List<object>();
        foreach (var id in selected)
        {
            // Sequentially: the bodies are the expensive part, and parallel fetches here buy
            // nothing but a shot at the rate limiter.
            using var message = await client.GetAsync(MessagePath(id), ct);
            var envelope = message.RootElement;

            var raw = envelope.TryGetProperty("raw", out var value) ? value.GetString() : null;
            if (string.IsNullOrEmpty(raw)) continue;

            var mime = MimeBuilder.FromRaw(raw);
            rendered.Add(MessageRenderer.Full(
                envelope, mime, mode, settings.MaxChars, settings.KeepQuotes, allHeaders: false));
        }

        var result = new Dictionary<string, object?>
        {
            ["account"] = client.Account.Name,
            ["threadId"] = threadId,
            ["messageCount"] = total,
            ["returned"] = rendered.Count,
            ["messages"] = rendered
        };

        if (rendered.Count < total)
            result["note"] = $"Showing the {rendered.Count} most recent of {total} messages. Raise --max-messages for more.";

        return result;
    }
}
