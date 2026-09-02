using System.ComponentModel;
using Gmail.Console.Auth;
using Gmail.Console.Infrastructure;
using Gmail.Console.Storage;
using Spectre.Console.Cli;

namespace Gmail.Console.Commands.Account;

public sealed class TestCommand : GmailCommand<TestCommand.Settings>
{
    public sealed class Settings : OutputSettings
    {
        [CommandArgument(0, "<NAME>")]
        [Description("Account name or email address")]
        public string Name { get; set; } = "";
    }

    protected override async Task<object?> RunAsync(CommandContext context, Settings settings, CancellationToken ct)
    {
        var store = SecretStoreFactory.Create();
        var account = AccountResolver.Resolve(settings.Name);

        var token = await TokenManager.GetAccessTokenAsync(account.Name, account.ClientRef, store, ct);
        var profile = await GmailProfile.FetchAsync(token, ct);

        var result = new Dictionary<string, object?>
        {
            ["name"] = account.Name,
            ["email"] = profile.EmailAddress,
            ["scopeProfile"] = account.ScopeProfile,
            ["tokenStatus"] = "valid",
            ["messagesTotal"] = profile.MessagesTotal,
            ["threadsTotal"] = profile.ThreadsTotal
        };

        // A mismatch means the browser was signed into a different Google account at login time.
        if (!profile.EmailAddress.Equals(account.Email, StringComparison.OrdinalIgnoreCase))
            result["warning"] =
                $"Config records this account as {account.Email}, but the credentials belong to {profile.EmailAddress}. " +
                $"Run: gmail account reauth {account.Name}";

        return result;
    }
}
