using System.ComponentModel;
using Gmail.Console.Auth;
using Gmail.Console.Infrastructure;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Gmail.Console.Commands.Draft;

public sealed class DeleteCommand : MailboxCommand<DeleteCommand.Settings>
{
    public sealed class Settings : AccountSettings
    {
        [CommandArgument(0, "<DRAFT-ID>")]
        public string DraftId { get; set; } = "";

        [CommandOption("--yes")]
        [Description("Skip the confirmation prompt")]
        public bool Yes { get; set; }
    }

    protected override async Task<object?> RunAsync(GmailApiClient client, Settings settings, CancellationToken ct)
    {
        ScopeProfiles.RequireDraft(client.Account.Name, client.Account.ScopeProfile);

        if (!settings.Yes)
        {
            var console = AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(System.Console.Error) });
            if (!console.Confirm($"Delete draft [green]{settings.DraftId}[/] from {client.Account.Email}?", false))
                throw GmailException.Invalid("Cancelled.");
        }

        await client.DeleteAsync($"drafts/{settings.DraftId}", ct);

        return new Dictionary<string, object?>
        {
            ["account"] = client.Account.Name,
            ["draftId"] = settings.DraftId,
            ["status"] = "deleted"
        };
    }
}
