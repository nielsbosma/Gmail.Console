using Gmail.Console.Infrastructure;
using Gmail.Console.Storage;

namespace Gmail.Console.Auth;

public sealed record ResolvedAccount(string Name, AccountConfig Config)
{
    public string Email => Config.Email;
    public string ScopeProfile => Config.ScopeProfile;
    public string ClientRef => Config.ClientRef;
}

/// <summary>
/// The account is always explicit — no stored default, no environment variable, no implicit
/// fallback when only one account exists. See spec decision G.
///
/// The failure this prevents is an agent working from a summarized transcript drafting from the
/// wrong mailbox: silent locally, and only discovered by the recipient.
/// </summary>
public static class AccountResolver
{
    public static ResolvedAccount Resolve(string? requested)
    {
        var config = ConfigStore.Load();

        if (string.IsNullOrWhiteSpace(requested))
            throw new GmailException(
                ErrorCode.NoAccount,
                "No account specified. Pass --account <name>.",
                Describe(config),
                "gmail account list");

        if (config.Accounts.TryGetValue(requested, out var byName))
            return new ResolvedAccount(CanonicalName(config, requested), byName);

        var byEmail = config.Accounts
            .Where(kv => kv.Value.Email.Equals(requested, StringComparison.OrdinalIgnoreCase))
            .Select(kv => (KeyValuePair<string, AccountConfig>?)kv)
            .FirstOrDefault();

        if (byEmail is not null)
            return new ResolvedAccount(byEmail.Value.Key, byEmail.Value.Value);

        throw new GmailException(
            ErrorCode.NoAccount,
            $"No account named '{requested}'.",
            Describe(config),
            "gmail account list");
    }

    private static string CanonicalName(GmailConfig config, string requested) =>
        config.Accounts.Keys.FirstOrDefault(k => k.Equals(requested, StringComparison.OrdinalIgnoreCase)) ?? requested;

    private static string Describe(GmailConfig config)
    {
        if (config.Accounts.Count == 0)
            return "No accounts are configured yet. Run 'gmail setup' and then 'gmail account add <name>'.";

        var listed = config.Accounts.Select(kv => $"{kv.Key} ({kv.Value.Email})");
        return "Configured accounts: " + string.Join(", ", listed);
    }
}
