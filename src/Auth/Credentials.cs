using System.Text.Json;
using System.Text.Json.Serialization;
using Gmail.Console.Infrastructure;
using Gmail.Console.Storage;

namespace Gmail.Console.Auth;

public sealed class ClientCredentials
{
    [JsonPropertyName("clientId")] public string ClientId { get; set; } = "";
    [JsonPropertyName("clientSecret")] public string ClientSecret { get; set; } = "";

    public static ClientCredentials Load(ISecretStore store, string clientRef = "default")
    {
        var key = clientRef == "default" ? SecretKeys.DefaultClient : "client:" + clientRef;
        var json = store.Get(key)
            ?? throw new GmailException(
                ErrorCode.AuthRequired,
                "No OAuth client credentials are configured.",
                "The Google Cloud client id and secret have not been set up on this machine.",
                "gmail setup");

        return JsonSerializer.Deserialize<ClientCredentials>(json)
               ?? throw new GmailException(ErrorCode.Error, "Stored client credentials are corrupt.", null, "gmail setup");
    }

    public void Save(ISecretStore store, string clientRef = "default")
    {
        var key = clientRef == "default" ? SecretKeys.DefaultClient : "client:" + clientRef;
        store.Set(key, JsonSerializer.Serialize(this));
    }

    public static bool Exists(ISecretStore store, string clientRef = "default")
    {
        var key = clientRef == "default" ? SecretKeys.DefaultClient : "client:" + clientRef;
        return store.Get(key) is not null;
    }
}

public sealed class StoredTokens
{
    [JsonPropertyName("refreshToken")] public string RefreshToken { get; set; } = "";
    [JsonPropertyName("accessToken")] public string? AccessToken { get; set; }
    [JsonPropertyName("expiresAt")] public DateTimeOffset? ExpiresAt { get; set; }
    [JsonPropertyName("scope")] public string? Scope { get; set; }

    public bool AccessTokenUsable =>
        !string.IsNullOrEmpty(AccessToken) &&
        ExpiresAt is not null &&
        ExpiresAt > DateTimeOffset.UtcNow.AddSeconds(60);

    public static StoredTokens? Load(ISecretStore store, string accountName)
    {
        var json = store.Get(SecretKeys.Account(accountName));
        return json is null ? null : JsonSerializer.Deserialize<StoredTokens>(json);
    }

    public void Save(ISecretStore store, string accountName) =>
        store.Set(SecretKeys.Account(accountName), JsonSerializer.Serialize(this));
}
