using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MimeKit;

namespace Gmail.Console.Mail;

public static partial class MessageRenderer
{
    public const string UntrustedOpen = "--- untrusted email content begins ---";
    public const string UntrustedClose = "--- untrusted email content ends ---";

    /// <summary>What a search hit looks like: enough to decide whether to fetch the whole thing.</summary>
    public static Dictionary<string, object?> Summary(JsonElement message)
    {
        var headers = Headers(message);
        var attachments = DescribeAttachments(message);

        var summary = new Dictionary<string, object?>
        {
            ["id"] = Text(message, "id"),
            ["threadId"] = Text(message, "threadId"),
            ["date"] = InternalDate(message),
            ["from"] = headers.GetValueOrDefault("from"),
            ["to"] = SplitAddresses(headers.GetValueOrDefault("to")),
            ["subject"] = headers.GetValueOrDefault("subject") ?? "(no subject)",
            ["snippet"] = Decode(Text(message, "snippet")),
            ["labels"] = Labels(message),
            ["hasAttachments"] = attachments.Count > 0,
            ["attachmentCount"] = attachments.Count
        };

        if (headers.GetValueOrDefault("cc") is { Length: > 0 } cc)
            summary["cc"] = SplitAddresses(cc);

        if (message.TryGetProperty("sizeEstimate", out var size) && size.TryGetInt64(out var bytes))
            summary["sizeEstimate"] = bytes;

        return summary;
    }

    /// <summary>A whole message, parsed from its raw RFC 5322 bytes by MimeKit.</summary>
    public static Dictionary<string, object?> Full(
        JsonElement envelope, MimeMessage mime, BodyMode mode, int maxChars, bool keepQuotes, bool allHeaders)
    {
        var result = new Dictionary<string, object?>
        {
            ["id"] = Text(envelope, "id"),
            ["threadId"] = Text(envelope, "threadId"),
            ["date"] = mime.Date == default
                ? InternalDate(envelope)
                : mime.Date.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            ["from"] = mime.From.ToString(),
            ["to"] = mime.To.Select(a => a.ToString()).ToList(),
            ["subject"] = string.IsNullOrEmpty(mime.Subject) ? "(no subject)" : mime.Subject,
            ["labels"] = Labels(envelope)
        };

        if (mime.Cc.Count > 0) result["cc"] = mime.Cc.Select(a => a.ToString()).ToList();
        if (mime.ReplyTo.Count > 0) result["replyTo"] = mime.ReplyTo.Select(a => a.ToString()).ToList();
        if (!string.IsNullOrEmpty(mime.MessageId)) result["messageIdHeader"] = "<" + mime.MessageId + ">";

        var attachments = mime.Attachments
            .OfType<MimePart>()
            .Select(p => (object)new Dictionary<string, object?>
            {
                ["filename"] = p.FileName ?? "(unnamed)",
                ["mimeType"] = p.ContentType.MimeType
            })
            .ToList();

        result["hasAttachments"] = attachments.Count > 0;
        if (attachments.Count > 0) result["attachments"] = attachments;

        if (allHeaders)
            result["headers"] = mime.Headers.ToDictionary(h => h.Field, h => (object?)h.Value);

        if (mode == BodyMode.Snippet)
        {
            result["body"] = Decode(Text(envelope, "snippet"));
            return result;
        }

        if (mode == BodyMode.None) return result;

        var body = ExtractBody(mime, mode);
        if (!keepQuotes) body = TrimQuotedReply(body);
        body = Normalize(body);

        var (truncated, omitted, total) = Truncate(body, maxChars);
        if (omitted > 0)
        {
            truncated += $"\n\n[truncated: {omitted:N0} of {total:N0} characters omitted — " +
                         "rerun with --max-chars 0 for the full body]";
            result["truncated"] = true;
        }

        result["body"] = $"{UntrustedOpen}\n{truncated}\n{UntrustedClose}";
        return result;
    }

    private static string ExtractBody(MimeMessage mime, BodyMode mode)
    {
        var text = mime.TextBody;
        var html = mime.HtmlBody;

        // A text/plain part is not always plain text. Helpdesks that forward a ticket - Freshdesk
        // is the one that turned up here - put marked-up HTML in it, and returning that verbatim
        // hands back a wall of divs under a field that promised markdown. Converting it recovers
        // the links and line breaks that stripping the tags would throw away.
        var plain = !string.IsNullOrWhiteSpace(text)
            ? (LooksLikeHtml(text!) ? HtmlToMarkdown(text!) : text)
            : null;

        return mode switch
        {
            BodyMode.Html => html ?? text ?? "",
            BodyMode.Text => plain ?? HtmlToMarkdown(html ?? ""),
            // Markdown: the plain part is already close to markdown and free of layout tables,
            // so prefer it; convert the HTML part only when there isn't one.
            _ => plain ?? HtmlToMarkdown(html ?? "")
        };
    }

    /// <summary>
    /// Block-level tags only. Someone writing "use a &lt;canvas&gt; element" in a genuinely plain
    /// message must not trip this, so an inline tag or a bare angle bracket is not enough.
    /// </summary>
    public static bool LooksLikeHtml(string text) => HtmlBlockTag().IsMatch(text);

    public static string HtmlToMarkdown(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return "";

        // ReverseMarkdown passes through the contents of script and style elements, which in a
        // marketing email is many kilobytes of CSS.
        html = ScriptOrStyle().Replace(html, " ");

        var converter = new ReverseMarkdown.Converter(new ReverseMarkdown.Config
        {
            GithubFlavored = true,
            Tags = { Unknown = ReverseMarkdown.Config.UnknownTagsOption.Bypass },
            Links = { SmartHref = true },
            Formatting = { RemoveComments = true, CleanupSpaces = true }
        });

        // ReverseMarkdown leaves a table nested inside a table cell as literal HTML, and
        // Unknown = Bypass passes through anything else it does not recognise. A mail
        // protection service's challenge page comes back as twenty kilobytes of raw markup
        // under a field that promised markdown, which then lands in a model's context.
        return StripTags(converter.Convert(html));
    }

    /// <summary>
    /// Removes HTML tags that survived conversion, leaving their text. Deliberately narrow:
    /// an address in angle brackets — "Paul &lt;paul@example.com&gt;" — is not a tag and has to
    /// survive, so the pattern requires a tag name followed by whitespace, '/' or '&gt;'.
    /// </summary>
    public static string StripTags(string markdown)
    {
        if (string.IsNullOrEmpty(markdown) || !markdown.Contains('<')) return markdown;

        markdown = HtmlComment().Replace(markdown, "");
        markdown = HtmlTag().Replace(markdown, "");

        // Only spaces and tabs: collapsing newlines here would flatten every paragraph in
        // every message, and Normalize already handles the vertical whitespace.
        return SpaceRun().Replace(markdown, " ");
    }

    /// <summary>
    /// Drops the quoted parent from a reply — the attribution line and everything after it, plus
    /// any trailing run of "&gt;"-prefixed lines. Signature blocks after "-- " are kept: they
    /// often carry the phone number or title someone asked us to find.
    /// </summary>
    public static string TrimQuotedReply(string body)
    {
        if (string.IsNullOrEmpty(body)) return body;

        var lines = body.Replace("\r\n", "\n").Split('\n');
        var cut = lines.Length;

        for (var i = 0; i < lines.Length; i++)
        {
            if (IsAttributionAt(lines, i) || OriginalMessageLine().IsMatch(lines[i])
                || IsForwardedHeaderAt(lines, i))
            {
                cut = i;
                break;
            }
        }

        while (cut > 0 && (lines[cut - 1].StartsWith('>') || string.IsNullOrWhiteSpace(lines[cut - 1])))
            cut--;

        return cut == lines.Length ? body : string.Join('\n', lines[..cut]).TrimEnd();
    }

    /// <summary>
    /// "On &lt;date&gt;, &lt;person&gt; wrote:" is one sentence but not reliably one line. Apple Mail
    /// and Outlook on the web put it in a table or a run of divs, and converting that to text
    /// leaves it broken across up to four indented lines:
    /// <code>
    ///     On
    ///     Thu, 2 Jul at  6:06 PM
    ///     ,  Paul Dougan &lt;pdougan@me.com&gt;  wrote:
    /// </code>
    /// Matching only a whole line silently keeps every one of those quotes, which is how a
    /// two-sentence reply comes back carrying the entire thread beneath it.
    /// </summary>
    private static bool IsAttributionAt(string[] lines, int index)
    {
        if (!AttributionStart().IsMatch(lines[index])) return false;
        if (AttributionLine().IsMatch(lines[index])) return true;

        // Only a wrapped attribution continues onto the next line, and it does so as short
        // fragments with no paragraph break. Prose that happens to open with "On" and end in
        // "wrote:" several lines later is a sentence, not a quote header — joining across a
        // blank line or a full-width line would cut the message in half.
        var window = new StringBuilder(lines[index].Trim());
        for (var next = index + 1; next < lines.Length && next <= index + 4; next++)
        {
            var line = lines[next].Trim();
            if (line.Length == 0 || lines[next - 1].Trim().Length > 60) return false;

            window.Append(' ').Append(line);
            if (AttributionLine().IsMatch(Collapse(window.ToString()))) return true;
        }

        return false;
    }

    /// <summary>
    /// Outlook quotes by pasting the parent's headers rather than an attribution line. "From:"
    /// alone is too common in prose to cut on, so require the "Sent:"/"Date:" that follows it.
    /// </summary>
    private static bool IsForwardedHeaderAt(string[] lines, int index)
    {
        if (!HeaderFrom().IsMatch(lines[index])) return false;

        for (var span = 1; span <= 3 && index + span < lines.Length; span++)
            if (HeaderSentOrDate().IsMatch(lines[index + span])) return true;

        return false;
    }

    private static string Collapse(string value) => WhitespaceRun().Replace(value, " ").Trim();

    /// <summary>Cuts at a paragraph boundary where one is available, so the tail isn't mid-sentence.</summary>
    public static (string Text, int Omitted, int Total) Truncate(string body, int maxChars)
    {
        if (maxChars <= 0 || body.Length <= maxChars) return (body, 0, body.Length);

        var window = body[..maxChars];
        var boundary = window.LastIndexOf("\n\n", StringComparison.Ordinal);
        if (boundary < maxChars / 2) boundary = window.LastIndexOf('\n');
        if (boundary < maxChars / 2) boundary = maxChars;

        var kept = body[..boundary].TrimEnd();
        return (kept, body.Length - kept.Length, body.Length);
    }

    /// <summary>
    /// Collapses runs of blank lines, normalizes line endings, and strips trailing whitespace so
    /// the result can be emitted as a YAML literal block rather than one escaped line.
    /// </summary>
    public static string Normalize(string body)
    {
        var lines = body.Replace("\r\n", "\n").Replace('\r', '\n')
            .Split('\n')
            .Select(l => l.Replace("\t", "    ").TrimEnd());

        var output = new StringBuilder();
        var blanks = 0;
        foreach (var line in lines)
        {
            if (line.Length == 0)
            {
                if (++blanks > 2) continue;
            }
            else blanks = 0;

            output.Append(line).Append('\n');
        }

        return output.ToString().Trim('\n');
    }

    public static Dictionary<string, string> Headers(JsonElement message)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!message.TryGetProperty("payload", out var payload)) return headers;
        if (!payload.TryGetProperty("headers", out var list) || list.ValueKind != JsonValueKind.Array) return headers;

        foreach (var header in list.EnumerateArray())
        {
            var name = header.TryGetProperty("name", out var n) ? n.GetString() : null;
            var value = header.TryGetProperty("value", out var v) ? v.GetString() : null;
            if (name is not null && value is not null) headers[name] = value;
        }
        return headers;
    }

    /// <summary>Every part carrying a filename, flattened out of the MIME tree.</summary>
    public static List<AttachmentPart> DescribeAttachments(JsonElement message)
    {
        var found = new List<AttachmentPart>();
        if (message.TryGetProperty("payload", out var payload)) Walk(payload, found);
        return found;

        static void Walk(JsonElement part, List<AttachmentPart> found)
        {
            var filename = part.TryGetProperty("filename", out var f) ? f.GetString() : null;
            if (!string.IsNullOrEmpty(filename))
            {
                var body = part.TryGetProperty("body", out var b) ? b : default;
                found.Add(new AttachmentPart(
                    AttachmentId: body.ValueKind == JsonValueKind.Object && body.TryGetProperty("attachmentId", out var id)
                        ? id.GetString() ?? ""
                        : "",
                    Filename: filename,
                    MimeType: part.TryGetProperty("mimeType", out var m) ? m.GetString() ?? "application/octet-stream" : "application/octet-stream",
                    Size: body.ValueKind == JsonValueKind.Object && body.TryGetProperty("size", out var s) && s.TryGetInt64(out var bytes) ? bytes : 0,
                    Inline: IsInline(part)));
            }

            if (part.TryGetProperty("parts", out var parts) && parts.ValueKind == JsonValueKind.Array)
                foreach (var child in parts.EnumerateArray()) Walk(child, found);
        }

        static bool IsInline(JsonElement part)
        {
            if (!part.TryGetProperty("headers", out var headers) || headers.ValueKind != JsonValueKind.Array)
                return false;

            foreach (var header in headers.EnumerateArray())
            {
                var name = header.TryGetProperty("name", out var n) ? n.GetString() : null;
                var value = header.TryGetProperty("value", out var v) ? v.GetString() : null;
                if (name is null || value is null) continue;
                if (name.Equals("Content-Disposition", StringComparison.OrdinalIgnoreCase) &&
                    value.StartsWith("inline", StringComparison.OrdinalIgnoreCase)) return true;
                if (name.Equals("Content-ID", StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }
    }

    public static string? InternalDate(JsonElement message)
    {
        if (!message.TryGetProperty("internalDate", out var value)) return null;

        var raw = value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
        return long.TryParse(raw, out var ms)
            ? DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ")
            : null;
    }

    public static List<string> Labels(JsonElement message) =>
        message.TryGetProperty("labelIds", out var labels) && labels.ValueKind == JsonValueKind.Array
            ? labels.EnumerateArray().Select(l => l.GetString() ?? "").Where(l => l.Length > 0).ToList()
            : [];

    public static List<string> SplitAddresses(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return [];
        try
        {
            return InternetAddressList.Parse(value).Select(a => a.ToString()).ToList();
        }
        catch (ParseException)
        {
            return [value];
        }
    }

    private static string? Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) ? value.GetString() : null;

    /// <summary>Gmail HTML-escapes snippets.</summary>
    private static string? Decode(string? snippet) =>
        snippet is null ? null : System.Net.WebUtility.HtmlDecode(snippet);

    [GeneratedRegex(@"<(script|style)\b[^>]*>.*?</\1>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex ScriptOrStyle();

    [GeneratedRegex(@"^\s*(On|Den|Am|Le|El)\b.{0,200}\b(wrote|skrev|schrieb|a écrit|escribió):\s*$",
        RegexOptions.IgnoreCase)]
    private static partial Regex AttributionLine();

    [GeneratedRegex(@"^\s*-{2,}\s*(Original Message|Ursprungligt meddelande|Forwarded message)\s*-{2,}\s*$",
        RegexOptions.IgnoreCase)]
    private static partial Regex OriginalMessageLine();

    /// <summary>The first word of an attribution, which may be all this line holds.</summary>
    [GeneratedRegex(@"^\s*(On|Den|Am|Le|El)\b", RegexOptions.IgnoreCase)]
    private static partial Regex AttributionStart();

    [GeneratedRegex(@"^\s*\*{0,2}From:\*{0,2}\s*\S", RegexOptions.IgnoreCase)]
    private static partial Regex HeaderFrom();

    [GeneratedRegex(@"^\s*\*{0,2}(Sent|Date|Skickat|Gesendet):\*{0,2}\s*\S", RegexOptions.IgnoreCase)]
    private static partial Regex HeaderSentOrDate();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRun();

    [GeneratedRegex(@"[ \t]{2,}")]
    private static partial Regex SpaceRun();

    [GeneratedRegex(@"<(?:div|table|tbody|tr|td|p|br|blockquote|body|html)\b[^<>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex HtmlBlockTag();

    [GeneratedRegex(@"<!--.*?-->|<!\[[^\]]*\]>|<!\[endif\]-->", RegexOptions.Singleline)]
    private static partial Regex HtmlComment();

    /// <summary>
    /// A tag name must be followed by whitespace, '&gt;', or the '/&gt;' of a self-closing tag.
    /// That is what keeps "&lt;paul@example.com&gt;" out of the match — '@' ends the name and
    /// fails the lookahead — and, less obviously, a markdown autolink: in
    /// "&lt;https://example.com/a&gt;" the name can only run to "https:", whose next characters
    /// are "//" rather than "/&gt;". Admitting a bare '/' here silently deletes every autolink.
    /// </summary>
    [GeneratedRegex(@"</?[A-Za-z][A-Za-z0-9:-]*(?=[\s>]|/>)[^<>]*>", RegexOptions.Singleline)]
    private static partial Regex HtmlTag();
}

public sealed record AttachmentPart(string AttachmentId, string Filename, string MimeType, long Size, bool Inline);
