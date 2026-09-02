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
                    "Refresh tokens issued while publishing status is Testing are revoked by Google after 7 days. " +
                    "Publish the app first (Google Auth Platform -> Audience -> Publish app), " +
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
        It takes about five minutes.

          1. Create a project (any name, e.g. "gmail-cli"):
             https://console.cloud.google.com/projectcreate

          2. Enable the Gmail API for it:
             https://console.cloud.google.com/apis/library/gmail.googleapis.com
             -> Enable.

          3. Open the Google Auth Platform:
             https://console.cloud.google.com/auth/overview
             If it offers a "Get started" flow, run through it. Choose audience
             "External" -- unless this is a Workspace-only tool, in which case
             "Internal" also lets you skip step 6.

          4. Left nav -> Branding. Fill in and Save:
               App name
               User support email
               Developer contact information (your email, at the bottom)
             Logo, app domain and authorized domains are all optional for a desktop
             client.

             Do not skip this. Until Branding is complete, the Audience page shows
             "Your app's OAuth configuration is incomplete" and the "Publish app"
             button in step 6 stays greyed out with no explanation of which field
             is missing.

          5. Left nav -> Data Access -> "Add or remove scopes". Add these two, then
             Update and Save:
               https://www.googleapis.com/auth/gmail.readonly     (search and read)
               https://www.googleapis.com/auth/gmail.compose      (drafts and replies)
             They will be listed as "restricted". That is expected.

          6. *** Left nav -> Audience -> "Publish app". ***
             Publishing status goes from Testing to In production. Leave it unverified.
             (Greyed out? Go back to step 4.)

             This is the step everyone skips. While publishing status is Testing,
             Google revokes every refresh token after 7 DAYS. Everything works, then
             breaks with invalid_grant a week later, long after you have stopped
             connecting it to setup. An unverified production app is allowed, keeps
             refresh tokens alive indefinitely, is capped at 100 users, and costs one
             extra click on a "Google hasn't verified this app" screen at login.

          7. Left nav -> Clients -> "Create OAuth client".
             Application type: Desktop app. Create, then copy the client ID and
             client secret.

          8. Paste them below. Then run:  gmail account add <name>

        If a guide you are following mentions "APIs & Services -> OAuth consent screen",
        it predates the current console: that page is now the Google Auth Platform, split
        across Branding (app name, support email, contact), Audience (user type,
        publishing status, test users) and Data Access (scopes). OAuth clients moved from
        Credentials to Clients.

        Workspace alternative: on a Google Workspace domain you can use a service account
        with domain-wide delegation instead -- no consent screen and no token expiry, but
        it needs domain admin rights and does not work for consumer @gmail.com accounts.
        """;
}
