using System.Text;
using Gmail.Console.Infrastructure;

namespace Gmail.Console.Mail;

/// <summary>
/// <c>--body</c> takes a file path, not text. This inverts the usual CLI convention on purpose:
/// an agent composing prose into a shell argument will eventually mangle a quote, a newline or
/// an "ä", and the damage is invisible until a human reads the draft. The safe route has to be
/// the one you reach for without thinking. See spec decision H.
/// </summary>
public static class BodyInput
{
    public static string Resolve(string? bodyPath, string? bodyText)
    {
        if (bodyPath is not null && bodyText is not null)
            throw GmailException.Invalid(
                "Pass either --body or --body-text, not both.",
                "Use --body <path> for a file, or --body-text for literal inline text.");

        if (bodyText is not null) return bodyText;

        if (bodyPath is null)
            throw GmailException.Invalid(
                "No body supplied.",
                "Write the body to a file and pass its path: --body ./reply.md " +
                "(or --body - to read stdin, or --body-text for a one-liner).");

        if (bodyPath == "-")
            return System.Console.In.ReadToEnd();

        if (!File.Exists(bodyPath))
            throw GmailException.Invalid(
                $"--body expects a file path; '{Elide(bodyPath)}' is not a file.",
                "Write the body to a file and pass its path, or use --body-text for literal text.");

        // Strip a UTF-8 BOM — it would otherwise become an invisible first character of the mail.
        var text = File.ReadAllText(bodyPath, Encoding.UTF8);
        return text.Length > 0 && text[0] == '﻿' ? text[1..] : text;
    }

    private static string Elide(string value) =>
        value.Length <= 60 ? value : value[..57] + "...";
}
