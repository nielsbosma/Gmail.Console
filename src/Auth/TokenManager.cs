using System.Text.Json;
using Gmail.Console.Infrastructure;
using Gmail.Console.Storage;

namespace Gmail.Console.Auth;

public static class TokenManager
{
    /// <summary>
    /// Returns a usable access token, refreshing under a cross-process lock when needed.
    ///
    /// The lock matters: two agent invocations refreshing at the same moment would otherwise
    /// race, and Google may rotate the refresh token on refresh — so the loser would persist a
    /// token the server has already retired. See spec G13.
    /// </summary>
    public static async Task<string> GetAccessTokenAsync(
        string accountName, string clientRef, ISecretStore store, CancellationToken ct)
    {
        var tokens = StoredTokens.Load(store, accountName)
            ?? throw new GmailException(
                ErrorCode.AuthRequired,
                $"Account '{accountName}' has no stored credentials.",
                "The account is listed in config but its tokens are missing from the keystore.",
                $"gmail account reauth {accountName}");

        if (tokens.AccessTokenUsable) return tokens.AccessToken!;

        using var _ = await FileLock.AcquireAsync(ConfigStore.LockPath, ct);

        // Another process may have refreshed while we waited for the lock.
        tokens = StoredTokens.Load(store, accountName) ?? tokens;
        if (tokens.AccessTokenUsable) return tokens.AccessToken!;

        var client = ClientCredentials.Load(store, clientRef);
        var refreshed = await RefreshAsync(client, tokens, accountName, ct);
        refreshed.Save(store, accountName);
        return refreshed.AccessToken!;
    }

    private static async Task<StoredTokens> RefreshAsync(
        ClientCredentials client, StoredTokens tokens, string accountName, CancellationToken ct)
    {
        using var http = new HttpClient();
        var response = await http.PostAsync(OAuthFlow.TokenEndpoint, new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["client_id"] = client.ClientId,
                ["client_secret"] = client.ClientSecret,
                ["refresh_token"] = tokens.RefreshToken,
                ["grant_type"] = "refresh_token"
            }), ct);

        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            var summary = OAuthFlow.Summarize(body);

            // invalid_grant covers revocation, a password change, six months of inactivity, and
            // the seven-day expiry that a Testing-mode consent screen imposes. That last one is
            // by far the most common, and the least obvious, so it is named explicitly.
            if (summary.Contains("invalid_grant", StringComparison.OrdinalIgnoreCase))
                throw new GmailException(
                    ErrorCode.AuthRequired,
                    $"Account '{accountName}' needs to be re-authorized.",
                    "Google returned invalid_grant — the refresh token was revoked or has expired. " +
                    "If this account worked until about a week ago, the OAuth consent screen is probably " +
                    "still in Testing mode, which expires refresh tokens after 7 days.",
                    $"gmail account reauth {accountName}");

            throw new GmailException(
                ErrorCode.AuthRequired,
                $"Could not refresh the access token for '{accountName}'.",
                summary,
                $"gmail account reauth {accountName}");
        }

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        var expiresIn = root.TryGetProperty("expires_in", out var e) ? e.GetInt32() : 3600;

        return new StoredTokens
        {
            // Google rotates the refresh token only sometimes; keep the existing one otherwise.
            RefreshToken = root.TryGetProperty("refresh_token", out var r) && r.GetString() is { Length: > 0 } rotated
                ? rotated
                : tokens.RefreshToken,
            AccessToken = root.GetProperty("access_token").GetString(),
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresIn),
            Scope = root.TryGetProperty("scope", out var s) ? s.GetString() : tokens.Scope
        };
    }

    /// <summary>Revokes the grant at Google so removing an account does not leave it live.</summary>
    public static async Task<bool> RevokeAsync(StoredTokens tokens, CancellationToken ct)
    {
        try
        {
            using var http = new HttpClient();
            var response = await http.PostAsync(OAuthFlow.RevokeEndpoint, new FormUrlEncodedContent(
                new Dictionary<string, string> { ["token"] = tokens.RefreshToken }), ct);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            return false;
        }
    }
}
