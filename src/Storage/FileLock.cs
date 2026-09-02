namespace Gmail.Console.Storage;

/// <summary>
/// Cross-process exclusive lock, held for the duration of a token refresh.
///
/// Two agent invocations hitting an expired access token at the same moment would otherwise
/// both refresh and both write, and the loser's rotated refresh token is destroyed — which
/// locks the account out in a way that looks random. See spec G13.
/// </summary>
public sealed class FileLock : IDisposable
{
    private readonly FileStream _stream;

    private FileLock(FileStream stream) => _stream = stream;

    public static async Task<FileLock> AcquireAsync(string path, CancellationToken ct, int timeoutMs = 15000)
    {
        ConfigStore.EnsureDir();

        var deadline = Environment.TickCount64 + timeoutMs;
        var delay = 25;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None,
                    bufferSize: 1, FileOptions.DeleteOnClose);
                return new FileLock(stream);
            }
            catch (IOException)
            {
                if (Environment.TickCount64 > deadline)
                    throw new IOException($"Timed out waiting for the lock at {path}. Another gmail process may be stuck.");

                await Task.Delay(delay, ct);
                delay = Math.Min(delay * 2, 400);
            }
        }
    }

    public void Dispose() => _stream.Dispose();
}
