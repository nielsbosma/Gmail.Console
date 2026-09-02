using System.ComponentModel;
using Gmail.Console.Auth;
using Gmail.Console.Infrastructure;
using Gmail.Console.Storage;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Gmail.Console.Commands.Account;

public sealed class RemoveCommand : GmailCommand<RemoveCommand.Settings>
{
    public sealed class Settings : OutputSettings
    {
        [CommandArgument(0, "<NAME>")]
        [Description("Account name or email address")]
        public string Name { get; set; } = "";

        [CommandOption("--local-only")]
        [Description("Delete local credentials but leave the grant active in the Google account")]
        public bool LocalOnly { get; set; }

        [CommandOption("--yes")]
        [Description("Skip the confirmation prompt")]
        public bool Yes { get; set; }
    }

    protected override async Task<object?> RunAsync(CommandContext context, Settings settings, CancellationToken ct)
    {
        var store = SecretStoreFactory.Create();
        var account = AccountResolver.Resolve(settings.Name);

        if (!settings.Yes)
        {
            var console = AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(System.Console.Error) });
            if (!console.Confirm($"Remove account [green]{account.Name}[/] ({account.Email})?", false))
                throw GmailException.Invalid("Cancelled.");
        }

        var tokens = StoredTokens.Load(store, account.Name);

        // Deleting the local file alone would leave a live grant sitting in the Google account
        // indefinitely, which is not what "remove" means to anyone. See spec G04.
        var revoked = false;
        if (!settings.LocalOnly && tokens is not null)
            revoked = await TokenManager.RevokeAsync(tokens, ct);

        using (var _ = await FileLock.AcquireAsync(ConfigStore.LockPath, ct))
        {
            store.Delete(SecretKeys.Account(account.Name));

            var config = ConfigStore.Load();
            config.Accounts.Remove(account.Name);
            ConfigStore.Save(config);
        }

        var result = new Dictionary<string, object?>
        {
            ["status"] = "removed",
            ["name"] = account.Name,
            ["email"] = account.Email,
            ["revokedAtGoogle"] = settings.LocalOnly ? false : revoked
        };

        if (!settings.LocalOnly && !revoked)
            result["warning"] =
                "Local credentials were deleted but the grant could not be revoked at Google. " +
                "Remove it manually at https://myaccount.google.com/permissions";

        return result;
    }
}
