using System.Text.Json;
using System.Text.Json.Serialization;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.EventEmitters;
using YamlDotNet.Serialization.NamingConventions;

namespace Gmail.Console.Infrastructure;

public static class OutputHelper
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly ISerializer YamlSerializer = new SerializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
        .WithEventEmitter(next => new LiteralMultilineEmitter(next))
        .Build();

    public static void Write(object data, string format)
    {
        var pruned = Prune(data) ?? new Dictionary<string, object?>();

        var text = format.Equals("json", StringComparison.OrdinalIgnoreCase)
            ? JsonSerializer.Serialize(pruned, JsonOptions)
            : YamlSerializer.Serialize(pruned).TrimEnd();

        System.Console.Out.WriteLine(text);
    }

    /// <summary>
    /// Drops null entries recursively. Both serializers' "omit null" settings apply to object
    /// properties only, and our payloads are dictionaries — without this, an absent value shows
    /// up as a bare "cc:" line that an agent then has to reason about.
    /// </summary>
    private static object? Prune(object? value) => value switch
    {
        null => null,
        IDictionary<string, object?> map => map
            .Select(kv => (kv.Key, Value: Prune(kv.Value)))
            .Where(entry => entry.Value is not null)
            .ToDictionary(entry => entry.Key, entry => entry.Value),
        IEnumerable<object> list => list.Select(Prune).Where(item => item is not null).ToList(),
        _ => value
    };

    public static void WriteError(GmailException ex, string format)
    {
        var payload = new Dictionary<string, object?>
        {
            ["error"] = ex.Message,
            ["code"] = ErrorCodes.Name(ex.Code)
        };
        if (!string.IsNullOrWhiteSpace(ex.Detail)) payload["detail"] = ex.Detail;
        if (!string.IsNullOrWhiteSpace(ex.Remediation)) payload["remediation"] = ex.Remediation;

        var text = format.Equals("json", StringComparison.OrdinalIgnoreCase)
            ? JsonSerializer.Serialize(payload, JsonOptions)
            : YamlSerializer.Serialize(payload).TrimEnd();

        System.Console.Error.WriteLine(text);
    }

    /// <summary>Human-facing chatter always goes to stderr so stdout stays machine-readable.</summary>
    public static void Status(string message) => System.Console.Error.WriteLine(message);

    /// <summary>
    /// Emits multi-line strings as literal block scalars (<c>body: |</c>) instead of one
    /// escaped double-quoted line, which is what makes message bodies readable in output.
    /// </summary>
    private sealed class LiteralMultilineEmitter(IEventEmitter next) : ChainedEventEmitter(next)
    {
        public override void Emit(ScalarEventInfo eventInfo, IEmitter emitter)
        {
            if (eventInfo.Source.Value is string s && s.Contains('\n') && IsLiteralSafe(s))
                eventInfo.Style = ScalarStyle.Literal;

            base.Emit(eventInfo, emitter);
        }

        // A literal block scalar cannot round-trip trailing whitespace, tabs, or lone carriage
        // returns, and a first line that starts with a space needs an explicit indentation
        // indicator. Anything that would lose data falls back to quoting. Indented lines after
        // the first are fine, which matters: markdown lists and code blocks are full of them.
        private static bool IsLiteralSafe(string s)
        {
            if (s.Contains('\r') || s.Contains('\t')) return false;
            if (s.StartsWith(' ')) return false;

            foreach (var line in s.Split('\n'))
                if (line.Length > 0 && char.IsWhiteSpace(line[^1])) return false;

            return true;
        }
    }
}
