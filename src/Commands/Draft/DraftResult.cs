using System.Text.Json;
using Gmail.Console.Infrastructure;
using MimeKit;

namespace Gmail.Console.Commands.Draft;

public static class DraftResult
{
    /// <summary>
    /// Always returns the draft id, so a retrying agent can update rather than create a second
    /// draft — there is no server-side idempotency key on drafts.create. See spec G12.
    ///
    /// The webUrl is the handoff: an agent run ends with a link a human clicks to review and send.
    /// </summary>
    public static Dictionary<string, object?> Describe(
        JsonElement response, MimeMessage message, string accountName, string status)
    {
        var draftId = response.TryGetProperty("id", out var id) ? id.GetString() : null;

        string? messageId = null;
        string? threadId = null;
        if (response.TryGetProperty("message", out var inner))
        {
            messageId = inner.TryGetProperty("id", out var m) ? m.GetString() : null;
            threadId = inner.TryGetProperty("threadId", out var t) ? t.GetString() : null;
        }

        var result = new Dictionary<string, object?>
        {
            ["account"] = accountName,
            ["draftId"] = draftId,
            ["messageId"] = messageId,
            ["threadId"] = threadId,
            ["to"] = message.To.Select(a => a.ToString()).ToList(),
            ["subject"] = message.Subject
        };

        if (message.Cc.Count > 0) result["cc"] = message.Cc.Select(a => a.ToString()).ToList();
        if (message.Bcc.Count > 0) result["bcc"] = message.Bcc.Select(a => a.ToString()).ToList();

        var attachments = message.Attachments.OfType<MimePart>().Select(p => (object)(p.FileName ?? "(unnamed)")).ToList();
        result["attachments"] = attachments;

        if (draftId is not null)
            result["webUrl"] = $"https://mail.google.com/mail/u/0/#drafts?compose={draftId}";

        result["status"] = status;
        return result;
    }

    public static object Body(string raw, string threadId) =>
        string.IsNullOrEmpty(threadId)
            ? new { message = new { raw } }
            : (object)new { message = new { raw, threadId } };
}
