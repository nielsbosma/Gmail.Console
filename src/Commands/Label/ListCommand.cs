using System.Text.Json;
using Gmail.Console.Infrastructure;
using Spectre.Console.Cli;

namespace Gmail.Console.Commands.Label;

public sealed class ListCommand : MailboxCommand<ListCommand.Settings>
{
    public sealed class Settings : AccountSettings;

    protected override async Task<object?> RunAsync(GmailApiClient client, Settings settings, CancellationToken ct)
    {
        using var doc = await client.GetAsync("labels", ct);

        var labels = new List<object>();
        if (doc.RootElement.TryGetProperty("labels", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var label in list.EnumerateArray())
            {
                labels.Add(new Dictionary<string, object?>
                {
                    ["id"] = label.TryGetProperty("id", out var id) ? id.GetString() : null,
                    ["name"] = label.TryGetProperty("name", out var name) ? name.GetString() : null,
                    ["type"] = label.TryGetProperty("type", out var type) ? type.GetString() : null
                });
            }
        }

        return new Dictionary<string, object?>
        {
            ["account"] = client.Account.Name,
            ["count"] = labels.Count,
            ["labels"] = labels
        };
    }
}
