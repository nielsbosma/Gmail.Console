using Gmail.Console.Infrastructure;
using Gmail.Console.Mail;

namespace Gmail.Console.Tests;

/// <summary>
/// Attachment filenames are chosen by whoever sent the mail, which makes this a security
/// boundary rather than a formatting concern.
/// </summary>
public class AttachmentWriterTests
{
    [Theory]
    [InlineData("invoice.pdf", "invoice.pdf")]
    [InlineData("../../.ssh/authorized_keys", "authorized_keys")]
    [InlineData("..\\..\\windows\\system32\\evil.dll", "evil.dll")]
    [InlineData("/etc/passwd", "passwd")]
    [InlineData("C:\\Windows\\notepad.exe", "notepad.exe")]
    [InlineData("report:2026*final?.pdf", "report_2026_final_.pdf")]
    [InlineData("  spaced  .pdf  ", "spaced  .pdf")]
    public void Sanitize_reduces_to_a_safe_leaf(string input, string expected) =>
        Assert.Equal(expected, AttachmentWriter.Sanitize(input, 1));

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("..")]
    [InlineData("...")]
    [InlineData("/")]
    public void Sanitize_falls_back_when_nothing_survives(string? input) =>
        Assert.Equal("attachment-7.bin", AttachmentWriter.Sanitize(input, 7));

    [Theory]
    [InlineData("CON.pdf")]
    [InlineData("nul.txt")]
    [InlineData("LPT1.doc")]
    public void Sanitize_escapes_reserved_windows_device_names(string input) =>
        Assert.StartsWith("_", AttachmentWriter.Sanitize(input, 1));

    [Fact]
    public void Sanitize_caps_absurd_lengths()
    {
        var name = new string('a', 500) + ".pdf";
        Assert.Equal(180, AttachmentWriter.Sanitize(name, 1).Length);
    }

    [Fact]
    public void ResolvePath_stays_inside_the_output_directory()
    {
        var dir = NewTempDir();
        var path = AttachmentWriter.ResolvePath(dir, AttachmentWriter.Sanitize("../escape.pdf", 1), overwrite: false);

        Assert.Equal(Path.Combine(Path.GetFullPath(dir), "escape.pdf"), path);
    }

    [Fact]
    public void ResolvePath_rejects_a_name_that_would_escape()
    {
        var dir = NewTempDir();

        // Sanitize normally prevents this; ResolvePath is the second line of defence.
        Assert.Throws<GmailException>(() =>
            AttachmentWriter.ResolvePath(dir, Path.Combine("..", "escape.pdf"), overwrite: false));
    }

    [Fact]
    public void ResolvePath_suffixes_collisions()
    {
        var dir = NewTempDir();
        File.WriteAllText(Path.Combine(dir, "invoice.pdf"), "first");

        var second = AttachmentWriter.ResolvePath(dir, "invoice.pdf", overwrite: false);
        Assert.Equal(Path.Combine(dir, "invoice (2).pdf"), second);

        File.WriteAllText(second, "second");
        Assert.Equal(Path.Combine(dir, "invoice (3).pdf"),
            AttachmentWriter.ResolvePath(dir, "invoice.pdf", overwrite: false));
    }

    [Fact]
    public void ResolvePath_reuses_the_name_when_overwriting()
    {
        var dir = NewTempDir();
        File.WriteAllText(Path.Combine(dir, "invoice.pdf"), "first");

        Assert.Equal(Path.Combine(dir, "invoice.pdf"),
            AttachmentWriter.ResolvePath(dir, "invoice.pdf", overwrite: true));
    }

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "gmail-cli-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
