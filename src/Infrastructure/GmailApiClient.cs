using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Gmail.Console.Auth;
using Gmail.Console.Storage;

namespace Gmail.Console.Infrastructure;

public sealed class GmailApiClient : IDisposable
{
    private const string BaseUrl = "https://gmail.googleapis.com/gmail/v1/users/me/";

    private readonly HttpClient _http;
    private readonly ISecretStore _store;
    private readonly bool _verbose;

    public ResolvedAccount Account { get; }

    public GmailApiClient(ResolvedAccount account, ISecretStore store, bool verbose, int timeoutSeconds)
    {
        Account = account;
        _store = store;
        _verbose = verbose;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(timeoutSeconds) };
    }

    public Task<JsonDocument> GetAsync(string path, CancellationToken ct) =>
        SendAsync(HttpMethod.Get, path, null, ct);

    public Task<JsonDocument> PostAsync(string path, object body, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, path, body, ct);

    public Task<JsonDocument> PutAsync(string path, object body, CancellationToken ct) =>
        SendAsync(HttpMethod.Put, path, body, ct);

    public async Task DeleteAsync(string path, CancellationToken ct)
    {
        using var _ = await SendAsync(HttpMethod.Delete, path, null, ct);
    }

    private async Task<JsonDocument> SendAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        const int maxAttempts = 5;
        var delay = TimeSpan.FromSeconds(1);

        for (var attempt = 1; ; attempt++)
        {
            var token = await TokenManager.GetAccessTokenAsync(Account.Name, Account.ClientRef, _store, ct);

            using var request = new HttpRequestMessage(method, BaseUrl + path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (body is not null)
                request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

            if (_verbose)
                OutputHelper.Status($"{method} {BaseUrl}{path}");

            using var response = await _http.SendAsync(request, ct);
            var text = await response.Content.ReadAsStringAsync(ct);

            if (_verbose)
                OutputHelper.Status($"  -> {(int)response.StatusCode} {response.StatusCode} ({text.Length} bytes)");

            if (response.IsSuccessStatusCode)
                return string.IsNullOrWhiteSpace(text)
                    ? JsonDocument.Parse("{}")
                    : JsonDocument.Parse(text);

            if (ShouldRetry(response.StatusCode, text) && attempt < maxAttempts)
            {
                var wait = response.Headers.RetryAfter?.Delta ?? Jitter(delay);
                if (_verbose) OutputHelper.Status($"  retrying in {wait.TotalSeconds:0.0}s (attempt {attempt}/{maxAttempts})");
                await Task.Delay(wait, ct);
                delay *= 2;
                continue;
            }

            throw Translate(response.StatusCode, text, attempt);
        }
    }

    /// <summary>
    /// Attachments arrive base64url-encoded inside a JSON envelope rather than as raw bytes.
    /// </summary>
    public async Task<byte[]> GetAttachmentAsync(string messageId, string attachmentId, CancellationToken ct)
    {
        using var doc = await GetAsync($"messages/{messageId}/attachments/{attachmentId}", ct);
        var data = doc.RootElement.GetProperty("data").GetString() ?? "";
        return Base64UrlDecode(data);
    }

    public static byte[] Base64UrlDecode(string value)
    {
        var s = value.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
    }

    public static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static bool ShouldRetry(HttpStatusCode status, string body)
    {
        if (status == HttpStatusCode.TooManyRequests) return true;
        if ((int)status >= 500) return true;

        // Gmail reports per-user rate limiting as a 403 with a reason, not a 429.
        return status == HttpStatusCode.Forbidden && IsRateLimit(body);
    }

    private static bool IsRateLimit(string body) =>
        body.Contains("rateLimitExceeded", StringComparison.OrdinalIgnoreCase) ||
        body.Contains("userRateLimitExceeded", StringComparison.OrdinalIgnoreCase);

    private static TimeSpan Jitter(TimeSpan baseDelay) =>
        TimeSpan.FromMilliseconds(Random.Shared.Next(0, (int)baseDelay.TotalMilliseconds));

    private GmailException Translate(HttpStatusCode status, string body, int attempts)
    {
        var detail = ExtractMessage(body);

        return status switch
        {
            HttpStatusCode.Unauthorized => new GmailException(
                ErrorCode.AuthRequired,
                $"Account '{Account.Name}' was rejected by Google.",
                detail,
                $"gmail account reauth {Account.Name}"),

            HttpStatusCode.Forbidden when IsRateLimit(body) => new GmailException(
                ErrorCode.RateLimited,
                $"Gmail rate limit still exceeded after {attempts} attempts.",
                detail,
                "Wait a few seconds and retry, or lower --concurrency."),

            HttpStatusCode.Forbidden => new GmailException(
                ErrorCode.AuthRequired,
                "Gmail refused the request.",
                detail,
                $"The account may lack the required scope. Try: gmail account reauth {Account.Name} --scope-profile draft"),

            HttpStatusCode.TooManyRequests => new GmailException(
                ErrorCode.RateLimited,
                $"Gmail rate limit still exceeded after {attempts} attempts.",
                detail,
                "Wait a few seconds and retry."),

            HttpStatusCode.NotFound => new GmailException(
                ErrorCode.NotFound, "Not found.", detail),

            HttpStatusCode.BadRequest => new GmailException(
                ErrorCode.InvalidInput, "Gmail rejected the request as malformed.", detail),

            _ => new GmailException(
                ErrorCode.Error, $"Gmail returned {(int)status} {status}.", detail)
        };
    }

    private static string? ExtractMessage(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var error) &&
                error.TryGetProperty("message", out var message))
                return message.GetString();
        }
        catch (JsonException)
        {
            // Fall through to the raw body.
        }
        return body.Length > 400 ? body[..400] : body;
    }

    public void Dispose() => _http.Dispose();
}
