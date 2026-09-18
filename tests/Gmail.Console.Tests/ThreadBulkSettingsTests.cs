namespace Gmail.Console.Tests;

using ThreadGet = Gmail.Console.Commands.Thread.GetCommand;

/// <summary>
/// `thread get` takes either an id or a file of them, and the two shapes have different
/// output destinations. Getting that wrong is expensive in opposite directions: no id at
/// all fetches nothing, and a bulk fetch without --out-dir renders several thousand mail
/// bodies onto stdout.
/// </summary>
public class ThreadBulkSettingsTests
{
    private static ThreadGet.Settings Settings() => new() { Account = "work" };

    [Fact]
    public void A_single_thread_id_is_enough()
    {
        var settings = Settings();
        settings.ThreadId = "163d448a56f11f9c";

        Assert.True(settings.Validate().Successful);
    }

    [Fact]
    public void Neither_an_id_nor_a_file_is_refused()
    {
        Assert.False(Settings().Validate().Successful);
    }

    [Fact]
    public void An_id_and_a_file_together_are_refused()
    {
        var file = Path.GetTempFileName();
        try
        {
            var settings = Settings();
            settings.ThreadId = "163d448a56f11f9c";
            settings.IdsFrom = file;
            settings.OutDir = "./out";

            Assert.False(settings.Validate().Successful);
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void Bulk_requires_an_out_dir()
    {
        var file = Path.GetTempFileName();
        try
        {
            var settings = Settings();
            settings.IdsFrom = file;

            Assert.False(settings.Validate().Successful);

            settings.OutDir = "./threads";
            Assert.True(settings.Validate().Successful);
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void A_missing_ids_file_fails_before_any_network_call()
    {
        var settings = Settings();
        settings.IdsFrom = Path.Combine(Path.GetTempPath(), "no-such-ids-file.txt");
        settings.OutDir = "./threads";

        var result = settings.Validate();

        Assert.False(result.Successful);
        Assert.Contains("no such file", result.Message, StringComparison.OrdinalIgnoreCase);
    }
}
