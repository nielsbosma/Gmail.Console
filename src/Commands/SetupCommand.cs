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
                "[yellow]Is this app published to [bold]In production[/], or an [bold]Internal[/] Workspace app?[/]",
                false);

            if (!published)
                throw GmailException.Invalid(
                    "Setup stopped: publishing status is still Testing.",
                    "Refresh tokens issued while publishing status is Testing are revoked by Google after 7 days. " +
                    "Publish the app (Google Auth Platform -> Audience -> Publish app), or make it Internal if the " +
                    "project belongs to a Workspace organisation, then run: gmail setup. " +
                    "To proceed on Testing anyway -- add yourself under Audience -> Test users first, and expect to " +
                    "re-authorize weekly -- pass the credentials as arguments instead: " +
                    "gmail setup --client-id <id> --client-secret <secret>");
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

          4. Left nav -> Branding. Marked with * and required to save:
               App name
               User support email
               Developer contact information -> Email addresses (at the bottom)

             Publishing to External production in step 6 additionally requires:
               Application home page
               Application privacy policy link
               Authorized domains -> the domain both URLs live on

             Google does not inspect what is at those URLs, but without them the
             "Publish app" button stays greyed out. Its tooltip is the only place
             that says so:

               "Valid app name, support email, homepage url, and privacy policy
                url are required for switching the app to external production
                mode."

             Leave the app logo empty. Uploading one forces brand verification --
             a review measured in weeks -- and nothing here needs it.

             Save.

          5. Left nav -> Data Access -> "Add or remove scopes". Add these two, then
             Update and Save:
               https://www.googleapis.com/auth/gmail.readonly     (search and read)
               https://www.googleapis.com/auth/gmail.compose      (drafts and replies)
             They will be listed as "restricted". That is expected.

          6. *** Left nav -> Audience -> "Publish app". ***
             Publishing status goes from Testing to In production. Leave it unverified.
             (Greyed out? Hover it -- the tooltip names the missing field. Step 4.)

             This is the step everyone skips. While publishing status is Testing,
             Google revokes every refresh token after 7 DAYS. Everything works, then
             breaks with invalid_grant a week later, long after you have stopped
             connecting it to setup. An unverified production app is allowed, keeps
             refresh tokens alive indefinitely, is capped at 100 users, and costs one
             extra click on a "Google hasn't verified this app" screen at login.

             Two alternatives, if standing up a home page and a privacy policy for a
             personal CLI is not worth it:

               Audience -> "Make internal", when the project belongs to a Google
               Workspace organisation. Internal apps skip publishing entirely and
               their refresh tokens do not expire -- but only accounts in that
               organisation can authorize, so this is no use for consumer @gmail.com
               addresses or a domain in a different org.

               Or stay in Testing and add yourself under Audience -> Test users.
               Everything works; you will just be running "gmail account reauth"
               every 7 days until you publish. Use the scripted form of this command
               to skip the publish confirmation:
                 gmail setup --client-id <id> --client-secret <secret>

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
