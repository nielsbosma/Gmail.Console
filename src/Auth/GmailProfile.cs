using System.Net.Http.Headers;
using System.Text.Json;
using Gmail.Console.Infrastructure;

namespace Gmail.Console.Auth;

public sealed record Profile(string EmailAddress, long MessagesTotal, long ThreadsTotal, long HistoryId);

/// <summary>
/// Fetches the mailbox profile with a bare access token, before an account exists in config —
/// which is how <c>account add</c> learns the real address rather than trusting what was typed.
/// </summary>
public static class GmailProfile
{
    public static async Task<Profile> FetchAsync(string accessToken, CancellationToken ct)
    {
        using var http = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://gmail.googleapis.com/gmail/v1/users/me/profile");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var response = await http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            throw new GmailException(
                ErrorCode.AuthRequired,
                "Could not read the mailbox profile.",
                OAuthFlow.Summarize(body),
                "Check that the Gmail API is enabled in the Google Cloud project.");

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        return new Profile(
            root.TryGetProperty("emailAddress", out var e) ? e.GetString() ?? "" : "",
            Number(root, "messagesTotal"),
            Number(root, "threadsTotal"),
            Number(root, "historyId"));
    }

    private static long Number(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value)) return 0;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var n)) return n;
        return long.TryParse(value.GetString(), out var parsed) ? parsed : 0;
    }
}
