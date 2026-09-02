using Gmail.Console.Infrastructure;

namespace Gmail.Console.Tests;

public class OutputHelperTests
{
    private static string Capture(object payload, string format = "yaml")
    {
        var original = System.Console.Out;
        var writer = new StringWriter();
        try
        {
            System.Console.SetOut(writer);
            OutputHelper.Write(payload, format);
        }
        finally
        {
            System.Console.SetOut(original);
        }
        return writer.ToString();
    }

    [Fact]
    public void Multiline_bodies_use_a_literal_block()
    {
        var yaml = Capture(new Dictionary<string, object?>
        {
            ["body"] = "First line.\n\nSecond paragraph."
        });

        Assert.Contains("body: |", yaml);
        Assert.DoesNotContain("\\n", yaml);
    }

    [Fact]
    public void Indented_lines_after_the_first_stay_literal()
    {
        var yaml = Capture(new Dictionary<string, object?>
        {
            ["body"] = "Steps:\n\n  1. one\n  2. two"
        });

        Assert.Contains("body: |", yaml);
        Assert.Contains("1. one", yaml);
    }

    [Fact]
    public void Trailing_whitespace_falls_back_to_quoting_rather_than_losing_it()
    {
        var yaml = Capture(new Dictionary<string, object?> { ["body"] = "one \ntwo" });

        Assert.DoesNotContain("body: |", yaml);
    }

    [Fact]
    public void Single_line_values_stay_plain()
    {
        var yaml = Capture(new Dictionary<string, object?> { ["subject"] = "Quarterly numbers" });

        Assert.Equal("subject: Quarterly numbers", yaml.Trim());
    }

    [Fact]
    public void Null_values_are_omitted()
    {
        var yaml = Capture(new Dictionary<string, object?> { ["a"] = "x", ["b"] = null });

        Assert.DoesNotContain("b:", yaml);
    }

    [Fact]
    public void Json_format_is_available()
    {
        var json = Capture(new Dictionary<string, object?> { ["count"] = 2 }, "json");

        Assert.Contains("\"count\": 2", json);
    }
}
