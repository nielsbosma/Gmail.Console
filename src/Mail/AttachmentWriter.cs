using System.Text.RegularExpressions;
using Gmail.Console.Infrastructure;

namespace Gmail.Console.Mail;

/// <summary>
/// The filename on an attachment was chosen by whoever sent the mail. "../../.ssh/authorized_keys"
/// is a legal MIME filename. Everything here runs before a single byte is written. See spec G09.
/// </summary>
public static partial class AttachmentWriter
{
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    public static string Sanitize(string? filename, int index)
    {
        // Take the leaf under both separator conventions — the sender's platform is not ours.
        var leaf = (filename ?? "").Replace('\\', '/');
        leaf = leaf[(leaf.LastIndexOf('/') + 1)..];

        leaf = Unsafe().Replace(leaf, "_").Trim(' ', '.');

        if (leaf.Length == 0) return $"attachment-{index}.bin";

        // CON.pdf resolves to the console device on Windows, not to a file.
        var stem = Path.GetFileNameWithoutExtension(leaf);
        if (ReservedNames.Contains(stem)) leaf = "_" + leaf;

        return leaf.Length > 180 ? leaf[..180] : leaf;
    }

    /// <summary>Resolves a safe destination inside <paramref name="outDir"/>, avoiding collisions.</summary>
    public static string ResolvePath(string outDir, string safeName, bool overwrite)
    {
        var root = Path.GetFullPath(outDir);
        var candidate = Path.GetFullPath(Path.Combine(root, safeName));

        // Belt and braces: after sanitization this should be impossible, so if it ever fires
        // something is wrong and we stop rather than write outside the directory.
        if (!candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
            !candidate.Equals(root, StringComparison.Ordinal))
            throw new GmailException(ErrorCode.Error,
                $"Refusing to write '{safeName}' outside {root}.");

        if (overwrite || !File.Exists(candidate)) return candidate;

        var stem = Path.GetFileNameWithoutExtension(safeName);
        var extension = Path.GetExtension(safeName);
        for (var n = 2; n < 1000; n++)
        {
            var next = Path.Combine(root, $"{stem} ({n}){extension}");
            if (!File.Exists(next)) return next;
        }

        throw new GmailException(ErrorCode.Error, $"Too many files named like '{safeName}' in {root}.");
    }

    [GeneratedRegex(@"[^A-Za-z0-9._ -]")]
    private static partial Regex Unsafe();
}
