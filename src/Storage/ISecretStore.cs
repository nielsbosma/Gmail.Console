namespace Gmail.Console.Storage;

/// <summary>
/// Anything bearer-shaped — refresh tokens, access tokens, the OAuth client secret.
/// Keys are <c>client:default</c> and <c>account:{name}</c>.
/// </summary>
public interface ISecretStore
{
    /// <summary>Backend name, as reported by <c>gmail doctor</c>.</summary>
    string Name { get; }

    string? Get(string key);
    void Set(string key, string value);
    void Delete(string key);
}

public static class SecretKeys
{
    public const string DefaultClient = "client:default";
    public static string Account(string name) => "account:" + name.ToLowerInvariant();
}
