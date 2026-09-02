using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Gmail.Console.Infrastructure;

public class GlobalSettings : CommandSettings
{
    private static readonly HashSet<string> ValidFormats = new(StringComparer.OrdinalIgnoreCase) { "yaml", "json" };

    // Declared here without a [CommandOption] so that subclasses can attach the option with
    // their own vocabulary — agent-readme emits markdown, everything else yaml or json.
    // Spectre walks the whole inheritance chain, so attributing it here as well would register
    // --format twice and refuse to start.
    public virtual string Format { get; set; } = "yaml";

    [CommandOption("--no-color")]
    [Description("Disable colored output")]
    public bool NoColor { get; set; }

    [CommandOption("--verbose")]
    [Description("Print HTTP method, URL and status to stderr (credentials redacted)")]
    public bool Verbose { get; set; }

    [CommandOption("--timeout <SECONDS>")]
    [Description("HTTP timeout in seconds")]
    [DefaultValue(100)]
    public int TimeoutSeconds { get; set; } = 100;

    protected virtual ValidationResult ValidateFormat() =>
        ValidFormats.Contains(Format)
            ? ValidationResult.Success()
            : ValidationResult.Error($"Invalid format '{Format}'. Must be yaml or json.");

    public override ValidationResult Validate()
    {
        var format = ValidateFormat();
        if (!format.Successful) return format;
        if (TimeoutSeconds <= 0)
            return ValidationResult.Error("--timeout must be greater than zero.");
        return base.Validate();
    }

    /// <summary>The format used for the error envelope, which is never markdown.</summary>
    public string ErrorFormat => Format.Equals("json", StringComparison.OrdinalIgnoreCase) ? "json" : "yaml";
}

/// <summary>Adds the ordinary <c>--format yaml|json</c> option.</summary>
public class OutputSettings : GlobalSettings
{
    [CommandOption("--format <FORMAT>")]
    [Description("Output format: yaml or json")]
    [DefaultValue("yaml")]
    public override string Format { get; set; } = "yaml";
}

/// <summary>
/// Settings for every command that touches a mailbox. <c>--account</c> is required, but it is
/// validated in <see cref="Auth.AccountResolver"/> rather than declared <c>required</c> here,
/// so a missing value produces our own error envelope with the list of configured accounts
/// instead of Spectre's parse error. See spec decision G.
/// </summary>
public class AccountSettings : OutputSettings
{
    [CommandOption("-a|--account <ACCOUNT>")]
    [Description("Account name or email address (required)")]
    public string? Account { get; set; }
}
