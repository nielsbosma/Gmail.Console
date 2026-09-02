using System.ComponentModel;
using Gmail.Console.Auth;
using Gmail.Console.Infrastructure;
using Gmail.Console.Storage;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Gmail.Console.Commands.Account;

public sealed class ReauthCommand : GmailCommand<ReauthCommand.Settings>
{
    public sealed class Settings : OutputSettings
    {
        [CommandArgument(0, "<NAME>")]
        [Description("Account name or email address")]
        public string Name { get; set; } = "";

        [CommandOption("--scope-profile <PROFILE>")]
        [Description("Change the scope profile while re-authorizing: read or draft")]
        public string? ScopeProfile { get; set; }

        [CommandOption("--port <PORT>")]
        [Description("Fixed loopback port for the OAuth redirect")]
        public int Port { get; set; }

        public override ValidationResult Validate()
        {
            if (ScopeProfile is not null && !ScopeProfiles.Names.Contains(ScopeProfile.ToLowerInvariant()))
                return ValidationResult.Error($"--scope-profile must be one of: {string.Join(", ", ScopeProfiles.Names)}");
            return base.Validate();
        }
    }

    protected override async Task<object?> RunAsync(CommandContext context, Settings settings, CancellationToken ct)
    {
        var store = SecretStoreFactory.Create();
        var account = AccountResolver.Resolve(settings.Name);
        var profileName = (settings.ScopeProfile ?? account.ScopeProfile).ToLowerInvariant();

        var client = ClientCredentials.Load(store, account.ClientRef);
        var tokens = await OAuthFlow.AuthorizeAsync(client, ScopeProfiles.Scopes(profileName), settings.Port, ct);
        var profile = await GmailProfile.FetchAsync(tokens.AccessToken!, ct);

        // Signing in as a different address under the same name would silently repoint the
        // account at another mailbox, which is exactly the confusion this tool avoids elsewhere.
        if (!profile.EmailAddress.Equals(account.Email, StringComparison.OrdinalIgnoreCase))
            throw GmailException.Invalid(
                $"You signed in as {profile.EmailAddress}, but '{account.Name}' is {account.Email}.",
                $"Sign in as {account.Email}, or add the other mailbox separately: gmail account add <name>");

        using (var _ = await FileLock.AcquireAsync(ConfigStore.LockPath, ct))
        {
            tokens.Save(store, account.Name);

            var config = ConfigStore.Load();
            if (config.Accounts.TryGetValue(account.Name, out var entry))
            {
                entry.ScopeProfile = profileName;
                ConfigStore.Save(config);
            }
        }

        return new Dictionary<string, object?>
        {
            ["status"] = "reauthorized",
            ["name"] = account.Name,
            ["email"] = profile.EmailAddress,
            ["scopeProfile"] = profileName
        };
    }
}
