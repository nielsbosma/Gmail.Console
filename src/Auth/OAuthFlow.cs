using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Gmail.Console.Infrastructure;

namespace Gmail.Console.Auth;

/// <summary>
/// Loopback authorization code flow with PKCE. Google turned off the out-of-band redirect in
/// 2022, so a desktop client listens on 127.0.0.1 and catches the redirect itself.
///
/// A desktop browser is assumed (spec decision I). If the browser cannot be launched we print
/// the URL and keep listening, which still works from a browser on the same machine.
/// </summary>
public static class OAuthFlow
{
    private const string AuthEndpoint = "https://accounts.google.com/o/oauth2/v2/auth";
    public const string TokenEndpoint = "https://oauth2.googleapis.com/token";
    public const string RevokeEndpoint = "https://oauth2.googleapis.com/revoke";

    public static async Task<StoredTokens> AuthorizeAsync(
        ClientCredentials client, string[] scopes, int requestedPort, CancellationToken ct)
    {
        var port = requestedPort > 0 ? requestedPort : FindFreePort();
        var redirectUri = $"http://127.0.0.1:{port}/";

        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = Base64Url(RandomNumberGenerator.GetBytes(16));

        var authUrl = AuthEndpoint + "?" + Form(new Dictionary<string, string>
        {
            ["client_id"] = client.ClientId,
            ["redirect_uri"] = redirectUri,
            ["response_type"] = "code",
            ["scope"] = string.Join(' ', scopes),
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
            ["state"] = state,
            ["access_type"] = "offline",
            // Without prompt=consent Google withholds the refresh token on a repeat login,
            // which silently produces an account that cannot be refreshed.
            ["prompt"] = "consent"
        });

        using var listener = new HttpListener();
        listener.Prefixes.Add(redirectUri);
        try
        {
            listener.Start();
        }
        catch (HttpListenerException ex)
        {
            throw new GmailException(ErrorCode.Error,
                $"Could not listen on {redirectUri}.", ex.Message,
                "Try a different port with --port, or check whether another process holds it.");
        }

        OutputHelper.Status($"Opening your browser to authorize this account.");
        OutputHelper.Status($"If nothing opens, paste this into a browser on this machine:");
        OutputHelper.Status("");
        OutputHelper.Status("  " + authUrl);
        OutputHelper.Status("");
        OutputHelper.Status($"Listening on {redirectUri} — waiting up to 3 minutes.");

        TryOpenBrowser(authUrl);

        var code = await WaitForCodeAsync(listener, state, ct);
        return await ExchangeCodeAsync(client, code, verifier, redirectUri, ct);
    }

    private static async Task<string> WaitForCodeAsync(HttpListener listener, string expectedState, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));

        HttpListenerContext context;
        try
        {
            var contextTask = listener.GetContextAsync();
            var completed = await Task.WhenAny(contextTask, Task.Delay(Timeout.Infinite, timeout.Token));
            if (completed != contextTask)
                throw new GmailException(ErrorCode.Error,
                    "Timed out waiting for the Google consent redirect.",
                    "No response arrived on the loopback listener within 3 minutes.",
                    "Run the command again and complete the login in the browser.");

            context = await contextTask;
        }
        catch (OperationCanceledException)
        {
            throw new GmailException(ErrorCode.Error, "Authorization was cancelled.");
        }

        var query = context.Request.QueryString;
        var error = query["error"];
        var code = query["code"];
        var state = query["state"];

        var message = error is not null
            ? $"Authorization failed: {error}. You can close this tab."
            : "Authorized. You can close this tab and return to the terminal.";

        await RespondAsync(context, message);
        listener.Stop();

        if (error is not null)
            throw new GmailException(ErrorCode.AuthRequired, $"Google returned '{error}' instead of an authorization code.");

        if (state != expectedState)
            throw new GmailException(ErrorCode.Error,
                "The OAuth state parameter did not match.",
                "This can indicate a cross-site request forgery attempt, or a stale browser tab from an earlier run.");

        if (string.IsNullOrEmpty(code))
            throw new GmailException(ErrorCode.Error, "The redirect carried no authorization code.");

        return code;
    }

    private static async Task RespondAsync(HttpListenerContext context, string message)
    {
        var html = $"""
            <!doctype html><meta charset="utf-8"><title>gmail CLI</title>
            <body style="font:16px system-ui;margin:80px auto;max-width:32em;color:#14171c">
            <h1 style="font-size:20px">{WebUtility.HtmlEncode(message)}</h1>
            </body>
            """;
        var bytes = Encoding.UTF8.GetBytes(html);
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes);
        context.Response.Close();
    }

    private static async Task<StoredTokens> ExchangeCodeAsync(
        ClientCredentials client, string code, string verifier, string redirectUri, CancellationToken ct)
    {
        using var http = new HttpClient();
        var response = await http.PostAsync(TokenEndpoint, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["code"] = code,
            ["client_id"] = client.ClientId,
            ["client_secret"] = client.ClientSecret,
            ["code_verifier"] = verifier,
            ["redirect_uri"] = redirectUri,
            ["grant_type"] = "authorization_code"
        }), ct);

        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new GmailException(ErrorCode.AuthRequired,
                "Google rejected the token exchange.", Summarize(body),
                "Check that the client id and secret from gmail setup belong to a Desktop app OAuth client.");

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        var refresh = root.TryGetProperty("refresh_token", out var r) ? r.GetString() : null;
        if (string.IsNullOrEmpty(refresh))
            throw new GmailException(ErrorCode.AuthRequired,
                "Google did not return a refresh token.",
                "This happens when the account has already granted access and consent was not re-requested.",
                "Revoke the app at https://myaccount.google.com/permissions and run the command again.");

        var expiresIn = root.TryGetProperty("expires_in", out var e) ? e.GetInt32() : 3600;

        return new StoredTokens
        {
            RefreshToken = refresh,
            AccessToken = root.TryGetProperty("access_token", out var a) ? a.GetString() : null,
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresIn),
            Scope = root.TryGetProperty("scope", out var s) ? s.GetString() : null
        };
    }

    public static string Summarize(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var error = root.TryGetProperty("error", out var e) ? e.ToString() : null;
            var description = root.TryGetProperty("error_description", out var d) ? d.GetString() : null;
            return string.Join(": ", new[] { error, description }.Where(x => !string.IsNullOrEmpty(x)));
        }
        catch (JsonException)
        {
            return body.Length > 400 ? body[..400] : body;
        }
    }

    private static void TryOpenBrowser(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception)
        {
            // The URL is already printed above; a machine with no browser handler is not fatal here.
        }
    }

    private static int FindFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string Form(Dictionary<string, string> values) =>
        string.Join('&', values.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
}
