using System.ComponentModel;
using Gmail.Console.Infrastructure;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Gmail.Console.Commands.Draft;

/// <summary>
/// Shared body and attachment options. Note that <c>--body</c> takes a path — see
/// <see cref="Mail.BodyInput"/> for why that is inverted from the usual convention.
/// </summary>
public abstract class DraftContentSettings : AccountSettings
{
    [CommandOption("--body <PATH>")]
    [Description("Path to a file holding the message body, or - to read stdin")]
    public string? BodyPath { get; set; }

    [CommandOption("--body-text <TEXT>")]
    [Description("Literal body text, for one-liners")]
    public string? BodyText { get; set; }

    [CommandOption("--html")]
    [Description("Treat the body as HTML rather than Markdown")]
    public bool Html { get; set; }

    [CommandOption("--plain")]
    [Description("Send a plain-text body only, with no HTML alternative")]
    public bool Plain { get; set; }

    [CommandOption("--attach <PATH>")]
    [Description("Attach a file (repeatable)")]
    public string[] Attach { get; set; } = [];

    public string BodyFormat => Html ? "html" : Plain ? "plain" : "markdown";

    public override ValidationResult Validate()
    {
        if (Html && Plain)
            return ValidationResult.Error("Pass either --html or --plain, not both.");
        return base.Validate();
    }
}
