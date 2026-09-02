using Gmail.Console.Auth;
using Gmail.Console.Infrastructure;
using Gmail.Console.Storage;
using Spectre.Console.Cli;

namespace Gmail.Console.Commands;

/// <summary>
/// One call that answers "why isn't this working". Everything it can determine locally it
/// determines locally; the consent-screen publishing state is the one thing it cannot see,
/// so it is reported as an unverifiable item rather than silently omitted.
/// </summary>
public sealed class DoctorCommand : GmailCommand<DoctorCommand.Settings>
{
    public sealed class Settings : OutputSettings;

    protected override async Task<object?> RunAsync(CommandContext context, Settings settings, CancellationToken ct)
    {
        var checks = new List<object>();
        var problems = 0;

        ISecretStore? store = null;
        try
        {
            store = SecretStoreFactory.Create();
            checks.Add(Check("secret_store", "ok", $"Using the {store.Name} backend."));
        }
        catch (GmailException ex)
        {
            problems++;
            checks.Add(Check("secret_store", "fail", ex.Message, ex.Remediation));
        }

        var configExists = File.Exists(ConfigStore.ConfigPath);
        checks.Add(Check("config_file",
            configExists ? "ok" : "warn",
            configExists ? ConfigStore.ConfigPath : $"No config yet at {ConfigStore.ConfigPath}.",
            configExists ? null : "gmail setup"));

        var config = configExists ? ConfigStore.Load() : new GmailConfig();

        if (store is not null)
        {
            var hasClient = ClientCredentials.Exists(store);
            if (!hasClient) problems++;
            checks.Add(Check("oauth_client",
                hasClient ? "ok" : "fail",
                hasClient ? "Client id and secret are stored." : "No OAuth client credentials configured.",
                hasClient ? null : "gmail setup"));

            if (config.Accounts.Count == 0)
            {
                checks.Add(Check("accounts", "warn", "No accounts configured.", "gmail account add <name>"));
            }
            else
            {
                foreach (var (name, account) in config.Accounts.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
                {
                    try
                    {
                        var token = await TokenManager.GetAccessTokenAsync(name, account.ClientRef, store, ct);
                        var profile = await GmailProfile.FetchAsync(token, ct);
                        checks.Add(Check($"account:{name}", "ok",
                            $"{profile.EmailAddress} — {profile.MessagesTotal:N0} messages, scope profile '{account.ScopeProfile}'."));
                    }
                    catch (GmailException ex)
                    {
                        problems++;
                        checks.Add(Check($"account:{name}", "fail", ex.Message, ex.Remediation));
                    }
                    catch (HttpRequestException ex)
                    {
                        problems++;
                        checks.Add(Check($"account:{name}", "fail", "Could not reach Google: " + ex.Message));
                    }
                }
            }
        }

        var skew = await ClockSkewAsync(ct);
        if (skew is not null)
        {
            var seconds = Math.Abs(skew.Value.TotalSeconds);
            checks.Add(Check("clock",
                seconds < 60 ? "ok" : "warn",
                $"Local clock differs from Google's by {skew.Value.TotalSeconds:0} seconds.",
                seconds < 60 ? null : "Sync the system clock — OAuth rejects tokens with large skew."));
        }

        checks.Add(Check("consent_screen", "unverifiable",
            "Whether your app is published to 'In production' cannot be read from the API. " +
            "If accounts stop working with invalid_grant after about a week, this is the cause: " +
            "a publishing status of Testing expires refresh tokens after 7 days.",
            "https://console.cloud.google.com/auth/audience -> Publish app"));

        return new Dictionary<string, object?>
        {
            ["configDir"] = ConfigStore.ConfigDir,
            ["secretStore"] = store?.Name,
            ["accountCount"] = config.Accounts.Count,
            ["problems"] = problems,
            ["checks"] = checks
        };
    }

    private static Dictionary<string, object?> Check(string name, string status, string detail, string? remediation = null)
    {
        var entry = new Dictionary<string, object?>
        {
            ["check"] = name,
            ["status"] = status,
            ["detail"] = detail
        };
        if (remediation is not null) entry["remediation"] = remediation;
        return entry;
    }

    private static async Task<TimeSpan?> ClockSkewAsync(CancellationToken ct)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            using var response = await http.GetAsync("https://oauth2.googleapis.com/", ct);
            var serverTime = response.Headers.Date;
            return serverTime is null ? null : DateTimeOffset.UtcNow - serverTime.Value;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
