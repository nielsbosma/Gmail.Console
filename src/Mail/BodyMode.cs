using Gmail.Console.Infrastructure;

namespace Gmail.Console.Mail;

public enum BodyMode
{
    Markdown,
    Text,
    Html,
    None,
    Snippet
}

public static class BodyModes
{
    public static readonly string[] Names = ["markdown", "text", "html", "none", "snippet"];

    public static BodyMode Parse(string? value) => (value ?? "markdown").ToLowerInvariant() switch
    {
        "markdown" or "md" => BodyMode.Markdown,
        "text" or "plain" => BodyMode.Text,
        "html" => BodyMode.Html,
        "none" => BodyMode.None,
        "snippet" => BodyMode.Snippet,
        _ => throw GmailException.Invalid(
            $"Unknown body mode '{value}'.",
            "Use --body one of: " + string.Join(", ", Names))
    };

    public static bool IsValid(string? value) =>
        value is null || Names.Contains(value.ToLowerInvariant()) || value is "md" or "plain";
}
