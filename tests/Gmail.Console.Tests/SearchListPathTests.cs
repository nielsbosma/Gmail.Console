namespace Gmail.Console.Tests;

using Gmail.Console.Commands;

/// <summary>
/// The listing URL is where a search's cost is decided, and where --all resumes from.
/// Both are easy to break silently: a dropped pageToken turns --all into an infinite
/// first page, and a maxResults that ignores --max fetches more than the caller asked for.
/// </summary>
public class SearchListPathTests
{
    private static SearchCommand.Settings Settings() => new() { Account = "work" };

    [Fact]
    public void Query_is_escaped_into_the_url()
    {
        var path = SearchCommand.ListPath("in:sent has:attachment", 50, null, Settings());

        Assert.StartsWith("messages?maxResults=50", path);
        Assert.Contains("&q=in%3Asent%20has%3Aattachment", path);
        Assert.DoesNotContain("pageToken", path);
    }

    [Fact]
    public void Page_token_is_carried_so_all_can_advance()
    {
        var path = SearchCommand.ListPath("", 100, "abc+/=def", Settings());

        Assert.Contains("&pageToken=abc%2B%2F%3Ddef", path);
    }

    [Fact]
    public void Labels_repeat_and_spam_trash_is_opt_in()
    {
        var settings = Settings();
        settings.Labels = ["INBOX", "STARRED"];
        settings.IncludeSpamTrash = true;

        var path = SearchCommand.ListPath("", 20, null, settings);

        Assert.Contains("&labelIds=INBOX", path);
        Assert.Contains("&labelIds=STARRED", path);
        Assert.Contains("&includeSpamTrash=true", path);
    }

    [Fact]
    public void All_without_ids_only_is_refused_above_a_page_or_so()
    {
        // Every result would cost its own messages.get, so this is the shape that
        // exhausts the per-minute quota unattended.
        var settings = Settings();
        settings.All = true;
        settings.Max = 5000;

        Assert.False(settings.Validate().Successful);

        settings.IdsOnly = true;
        Assert.True(settings.Validate().Successful);
    }

    [Fact]
    public void All_with_a_small_max_stays_allowed_without_ids_only()
    {
        var settings = Settings();
        settings.All = true;
        settings.Max = 500;

        Assert.True(settings.Validate().Successful);
    }
}
