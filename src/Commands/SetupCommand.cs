using System.ComponentModel;
using Gmail.Console.Auth;
using Gmail.Console.Infrastructure;
using Gmail.Console.Storage;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Gmail.Console.Commands;

public sealed class SetupCommand : GmailCommand<SetupCommand.Settings>
{
    public sealed class Settings : OutputSettings
    {
        [CommandOption("--show")]
        [Description("Print the Google Cloud walkthrough and exit without storing anything")]
        public bool Show { get; set; }

        [CommandOption("--client-id <ID>")]
        [Description("OAuth client id (skips the prompt)")]
        public string? ClientId { get; set; }

        [CommandOption("--client-secret <SECRET>")]
        [Description("OAuth client secret (skips the prompt)")]
        public string? ClientSecret { get; set; }
    }

    protected override Task<object?> RunAsync(CommandContext context, Settings settings, CancellationToken ct)
    {
        if (settings.Show)
        {
            System.Console.Out.WriteLine(Guide);
            return Task.FromResult<object?>(null);
        }

        // Prompts and guidance go to stderr so stdout carries only the machine-readable result.
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Out = new AnsiConsoleOutput(System.Console.Error)
        });

        var scripted = !string.IsNullOrWhiteSpace(settings.ClientId) && !string.IsNullOrWhiteSpace(settings.ClientSecret);

        if (!scripted)
        {
            System.Console.Error.WriteLine(Guide);
            console.WriteLine();

            // The publishing state is invisible from the API — nothing in a token response says
            // which one issued it — so it has to be a checkpoint rather than a paragraph.
            var published = console.Confirm(
                "[yellow]Is the OAuth consent screen published to [bold]In production[/]?[/]", false);

            if (!published)
                throw GmailException.Invalid(
                    "Setup stopped: the OAuth consent screen is still in Testing mode.",
                    "Refresh tokens issued by a Testing-mode consent screen are revoked by Google after 7 days. " +
                    "Publish the app first (Console -> APIs & Services -> OAuth consent screen -> Publish app), " +
                    "then run: gmail setup");
        }

        var clientId = settings.ClientId ?? console.Prompt(
            new TextPrompt<string>("OAuth [green]client id[/]:").Validate(NotBlank));

        var clientSecret = settings.ClientSecret ?? console.Prompt(
            new TextPrompt<string>("OAuth [green]client secret[/]:").Secret().Validate(NotBlank));

        var store = SecretStoreFactory.Create();
        new ClientCredentials { ClientId = clientId.Trim(), ClientSecret = clientSecret.Trim() }.Save(store);

        return Task.FromResult<object?>(new Dictionary<string, object?>
        {
            ["status"] = "configured",
            ["secretStore"] = store.Name,
            ["configDir"] = ConfigStore.ConfigDir,
            ["nextStep"] = "gmail account add <name>"
        });
    }

    private static ValidationResult NotBlank(string value) =>
        string.IsNullOrWhiteSpace(value)
            ? ValidationResult.Error("Cannot be empty")
            : ValidationResult.Success();

    public const string Guide = """
        Setting up Gmail API access
        ===========================

        You need your own Google Cloud OAuth client. This tool does not ship one: a shared
        client would put every installation under one quota and one revocable credential.
        It takes about two minutes.

          1. Go to https://console.cloud.google.com/ and create a project (any name,
             e.g. "gmail-cli").

          2. APIs & Services -> Library -> search "Gmail API" -> Enable.

          3. APIs & Services -> OAuth consent screen.
             User type: External. (Internal if this is a Workspace-only tool -- then you
             can skip step 5.) Fill in an app name and your email as support contact.

          4. Add the scopes you want:
               https://www.googleapis.com/auth/gmail.readonly     (search and read)
               https://www.googleapis.com/auth/gmail.compose      (drafts and replies)
             Google will warn that these are "restricted" scopes. That is expected.

          5. *** Click "Publish app" to move the consent screen from Testing to
             In production. Leave it unverified. ***

             This is the step everyone skips. While the consent screen is in Testing,
             Google revokes every refresh token after 7 DAYS. Everything works, then
             breaks with invalid_grant a week later, long after you have stopped
             connecting it to setup. An unverified production app is allowed, keeps
             refresh tokens alive indefinitely, is capped at 100 users, and costs one
             extra click on a "Google hasn't verified this app" screen at login.

          6. Credentials -> Create credentials -> OAuth client ID ->
             Application type: Desktop app. Copy the client id and client secret.

          7. Paste them below. Then run:  gmail account add <name>

        Workspace alternative: on a Google Workspace domain you can use a service account
        with domain-wide delegation instead -- no consent screen and no token expiry, but
        it needs domain admin rights and does not work for consumer @gmail.com accounts.
        """;
}
