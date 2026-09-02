using Gmail.Console.Infrastructure;

namespace Gmail.Console.Auth;

/// <summary>
/// Named bundles of Gmail scopes. Note that there is no draft-only scope — creating a draft
/// requires <c>gmail.compose</c>, which also permits sending. The guard against an agent
/// sending mail is therefore the command surface (there is no send command), not the grant.
/// See spec G02 and decision B.
/// </summary>
public static class ScopeProfiles
{
    public const string Read = "read";
    public const string Draft = "draft";

    public static readonly string[] Names = [Read, Draft];

    public static string[] Scopes(string profile) => profile.ToLowerInvariant() switch
    {
        Read => ["https://www.googleapis.com/auth/gmail.readonly"],
        Draft =>
        [
            "https://www.googleapis.com/auth/gmail.readonly",
            "https://www.googleapis.com/auth/gmail.compose"
        ],
        _ => throw GmailException.Invalid(
            $"Unknown scope profile '{profile}'.",
            "Use --scope-profile read or --scope-profile draft.")
    };

    public static bool CanDraft(string profile) => profile.Equals(Draft, StringComparison.OrdinalIgnoreCase);

    public static void RequireDraft(string accountName, string profile)
    {
        if (CanDraft(profile)) return;
        throw new GmailException(
            ErrorCode.AuthRequired,
            $"Account '{accountName}' was authorized read-only and cannot create drafts.",
            $"Its scope profile is '{profile}'.",
            $"gmail account reauth {accountName} --scope-profile draft");
    }
}
