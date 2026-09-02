using System.ComponentModel;
using Gmail.Console.Auth;
using Gmail.Console.Infrastructure;
using Gmail.Console.Storage;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Gmail.Console.Commands.Account;

public sealed class AddCommand : GmailCommand<AddCommand.Settings>
{
    public sealed class Settings : OutputSettings
    {
        [CommandArgument(0, "[NAME]")]
        [Description("Short name for this account (prompted if omitted)")]
        public string? Name { get; set; }

        [CommandOption("--scope-profile <PROFILE>")]
        [Description("read (search and read only) or draft (also create drafts)")]
        [DefaultValue("draft")]
        public string ScopeProfile { get; set; } = "draft";

        [CommandOption("--port <PORT>")]
        [Description("Fixed loopback port for the OAuth redirect (default: an ephemeral one)")]
        public int Port { get; set; }

        public override ValidationResult Validate()
        {
            if (!ScopeProfiles.Names.Contains(ScopeProfile.ToLowerInvariant()))
                return ValidationResult.Error($"--scope-profile must be one of: {string.Join(", ", ScopeProfiles.Names)}");
            return base.Validate();
        }
    }

    protected override async Task<object?> RunAsync(CommandContext context, Settings settings, CancellationToken ct)
    {
        var store = SecretStoreFactory.Create();
        var client = ClientCredentials.Load(store);
        var profileName = settings.ScopeProfile.ToLowerInvariant();

        var tokens = await OAuthFlow.AuthorizeAsync(client, ScopeProfiles.Scopes(profileName), settings.Port, ct);
        var profile = await GmailProfile.FetchAsync(tokens.AccessToken!, ct);

        var config = ConfigStore.Load();
        var name = settings.Name ?? PromptForName(profile.EmailAddress);

        if (string.IsNullOrWhiteSpace(name))
            throw GmailException.Invalid("An account name is required.");

        name = name.Trim();

        if (config.Accounts.ContainsKey(name))
            throw GmailException.Invalid(
                $"An account named '{name}' already exists ({config.Accounts[name].Email}).",
                $"Pick a different name, or re-authorize the existing one: gmail account reauth {name}");

        using (var _ = await FileLock.AcquireAsync(ConfigStore.LockPath, ct))
        {
            // Persist the token first: a config entry with no credentials is a broken account,
            // whereas an orphaned secret is invisible and harmless.
            tokens.Save(store, name);

            config = ConfigStore.Load();
            config.Accounts[name] = new AccountConfig
            {
                Email = profile.EmailAddress,
                ScopeProfile = profileName,
                ClientRef = "default",
                AddedAt = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")
            };
            ConfigStore.Save(config);
        }

        return new Dictionary<string, object?>
        {
            ["status"] = "added",
            ["name"] = name,
            ["email"] = profile.EmailAddress,
            ["scopeProfile"] = profileName,
            ["messagesTotal"] = profile.MessagesTotal,
            ["secretStore"] = store.Name,
            ["nextStep"] = $"gmail search \"is:unread\" --account {name}"
        };
    }

    private static string PromptForName(string email)
    {
        var console = AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(System.Console.Error) });
        var suggestion = email.Split('@')[0];

        return console.Prompt(new TextPrompt<string>($"Name for [green]{email}[/]:")
            .DefaultValue(suggestion)
            .Validate(value => string.IsNullOrWhiteSpace(value)
                ? ValidationResult.Error("Cannot be empty")
                : ValidationResult.Success()));
    }
}
