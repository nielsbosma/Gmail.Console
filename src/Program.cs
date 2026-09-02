using System.Text;
using Gmail.Console.Commands;
using Gmail.Console.Infrastructure;
using Spectre.Console.Cli;

// Subject lines are full of non-ASCII; the default Windows codepage mangles them.
try
{
    System.Console.OutputEncoding = Encoding.UTF8;
}
catch (IOException)
{
    // No console attached (piped or redirected) — nothing to configure.
}

var app = new CommandApp();

app.Configure(config =>
{
    config.SetApplicationName("gmail");

    config.AddCommand<SetupCommand>("setup")
        .WithDescription("Set up Google Cloud OAuth credentials, with a walkthrough");

    config.AddBranch("account", account =>
    {
        account.SetDescription("Manage named Gmail accounts");
        account.AddCommand<Gmail.Console.Commands.Account.AddCommand>("add")
            .WithDescription("Log in to a Google account and store its credentials");
        account.AddCommand<Gmail.Console.Commands.Account.ListCommand>("list")
            .WithDescription("List configured accounts and their token status");
        account.AddCommand<Gmail.Console.Commands.Account.TestCommand>("test")
            .WithDescription("Verify one account's credentials against Google");
        account.AddCommand<Gmail.Console.Commands.Account.ReauthCommand>("reauth")
            .WithDescription("Re-run consent for an existing account");
        account.AddCommand<Gmail.Console.Commands.Account.RemoveCommand>("remove")
            .WithDescription("Revoke the grant at Google and delete local credentials");
    });

    config.AddCommand<SearchCommand>("search")
        .WithDescription("Search a mailbox and return message summaries");

    config.AddBranch("message", message =>
    {
        message.SetDescription("Read individual messages");
        message.AddCommand<Gmail.Console.Commands.Message.GetCommand>("get")
            .WithDescription("Fetch one message with headers and body");
        message.AddCommand<Gmail.Console.Commands.Message.AttachmentsCommand>("attachments")
            .WithDescription("List a message's attachments without downloading them");
    });

    config.AddBranch("thread", thread =>
    {
        thread.SetDescription("Read whole conversations");
        thread.AddCommand<Gmail.Console.Commands.Thread.GetCommand>("get")
            .WithDescription("Fetch a thread's messages in order");
    });

    config.AddBranch("attachment", attachment =>
    {
        attachment.SetDescription("Download attachments");
        attachment.AddCommand<Gmail.Console.Commands.Attachment.DownloadCommand>("download")
            .WithDescription("Save a message's attachments to disk");
    });

    config.AddBranch("label", label =>
    {
        label.SetDescription("Mailbox labels");
        label.AddCommand<Gmail.Console.Commands.Label.ListCommand>("list")
            .WithDescription("List labels with their ids");
    });

    config.AddBranch("draft", draft =>
    {
        draft.SetDescription("Create and manage drafts (nothing is ever sent)");
        draft.AddCommand<Gmail.Console.Commands.Draft.CreateCommand>("create")
            .WithDescription("Create a new draft");
        draft.AddCommand<Gmail.Console.Commands.Draft.ReplyCommand>("reply")
            .WithDescription("Create a correctly threaded reply draft");
        draft.AddCommand<Gmail.Console.Commands.Draft.ListCommand>("list")
            .WithDescription("List existing drafts");
        draft.AddCommand<Gmail.Console.Commands.Draft.GetCommand>("get")
            .WithDescription("Read a draft back");
        draft.AddCommand<Gmail.Console.Commands.Draft.UpdateCommand>("update")
            .WithDescription("Replace a draft's content");
        draft.AddCommand<Gmail.Console.Commands.Draft.DeleteCommand>("delete")
            .WithDescription("Discard a draft");
    });

    config.AddCommand<AgentReadmeCommand>("agent-readme")
        .WithDescription("Print the operating manual for an LLM agent");

    config.AddCommand<DoctorCommand>("doctor")
        .WithDescription("Diagnose configuration, credentials and connectivity");

    // Parse and validation failures get the same YAML envelope as everything else, so an agent
    // never has to distinguish "the CLI rejected this" from "Gmail rejected this".
    config.SetExceptionHandler((exception, _) =>
    {
        var error = exception is GmailException gmail
            ? gmail
            : new GmailException(ErrorCode.InvalidInput, exception.Message);

        OutputHelper.WriteError(error, "yaml");
        return (int)error.Code;
    });
});

return app.Run(args);
