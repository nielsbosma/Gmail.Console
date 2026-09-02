using System.ComponentModel;
using Gmail.Console.Auth;
using Gmail.Console.Infrastructure;
using Gmail.Console.Storage;
using Spectre.Console.Cli;

namespace Gmail.Console.Commands.Account;

public sealed class ListCommand : GmailCommand<ListCommand.Settings>
{
    public sealed class Settings : OutputSettings
    {
        [CommandOption("--check")]
        [Description("Probe each account against Google instead of reporting cached state")]
        public bool Check { get; set; }
    }

    protected override async Task<object?> RunAsync(CommandContext context, Settings settings, CancellationToken ct)
    {
        var store = SecretStoreFactory.Create();
        var config = ConfigStore.Load();
        var accounts = new List<object>();

        foreach (var (name, account) in config.Accounts.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            var entry = new Dictionary<string, object?>
            {
                ["name"] = name,
                ["email"] = account.Email,
                ["scopeProfile"] = account.ScopeProfile,
                ["tokenStatus"] = await StatusOf(name, account, store, settings.Check, ct)
            };

            if (!string.IsNullOrEmpty(account.AddedAt)) entry["addedAt"] = account.AddedAt;
            accounts.Add(entry);
        }

        return new Dictionary<string, object?>
        {
            ["accounts"] = accounts,
            ["count"] = accounts.Count,
            ["secretStore"] = store.Name,
            ["configDir"] = ConfigStore.ConfigDir
        };
    }

    private static async Task<string> StatusOf(
        string name, AccountConfig account, ISecretStore store, bool check, CancellationToken ct)
    {
        var tokens = StoredTokens.Load(store, name);
        if (tokens is null) return "missing_credentials";

        if (!check)
            // Derived without a network call: a cached unexpired access token proves the account
            // works right now, anything else is genuinely unknown until we ask.
            return tokens.AccessTokenUsable ? "valid" : "unknown";

        try
        {
            await TokenManager.GetAccessTokenAsync(name, account.ClientRef, store, ct);
            return "valid";
        }
        catch (GmailException ex) when (ex.Code == ErrorCode.AuthRequired)
        {
            return "needs_reauth";
        }
        catch (HttpRequestException)
        {
            return "unreachable";
        }
    }
}
