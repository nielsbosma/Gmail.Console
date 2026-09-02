using System.Diagnostics;
using Gmail.Console.Infrastructure;

namespace Gmail.Console.Storage;

public static class SecretStoreFactory
{
    private static ISecretStore? _cached;

    public static ISecretStore Create()
    {
        if (_cached is not null) return _cached;
        return _cached = Build();
    }

    private static ISecretStore Build()
    {
        var forced = Environment.GetEnvironmentVariable("GMAIL_SECRET_STORE");
        if (!string.IsNullOrWhiteSpace(forced))
        {
            return forced.ToLowerInvariant() switch
            {
                "dpapi" when OperatingSystem.IsWindows() => new DpapiSecretStore(),
                "keychain" => new KeychainSecretStore(),
                "libsecret" => new LibSecretStore(),
                "plaintext" => new PlaintextSecretStore(),
                _ => throw GmailException.Invalid(
                    $"GMAIL_SECRET_STORE='{forced}' is not a backend available on this platform.",
                    "Unset GMAIL_SECRET_STORE, or set it to one of: dpapi, keychain, libsecret, plaintext.")
            };
        }

        if (OperatingSystem.IsWindows()) return new DpapiSecretStore();

        if (OperatingSystem.IsMacOS())
        {
            if (CommandExists("security")) return new KeychainSecretStore();
            return Fallback("The macOS 'security' command was not found.");
        }

        if (CommandExists("secret-tool")) return new LibSecretStore();

        return Fallback(
            "libsecret is not installed, so there is no OS keystore to hold your refresh tokens. " +
            "Install it with: sudo apt install libsecret-tools  (or the equivalent for your distro).");
    }

    /// <summary>
    /// Degrading silently to a plaintext file would contradict the whole point of this tool,
    /// so it has to be asked for explicitly. See spec decision D.
    /// </summary>
    private static ISecretStore Fallback(string reason)
    {
        if (Environment.GetEnvironmentVariable("GMAIL_ALLOW_PLAINTEXT_STORE") == "1")
            return new PlaintextSecretStore();

        throw new GmailException(
            ErrorCode.Error,
            "No secure credential store is available on this machine.",
            reason,
            "Install a keystore, or set GMAIL_ALLOW_PLAINTEXT_STORE=1 to store credentials in a 0600 file instead.");
    }

    private static bool CommandExists(string command)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "/usr/bin/env",
                ArgumentList = { "which", command },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            });
            if (process is null) return false;
            process.WaitForExit(3000);
            return process.ExitCode == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
