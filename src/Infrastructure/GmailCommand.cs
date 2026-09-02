using Gmail.Console.Auth;
using Gmail.Console.Storage;
using Spectre.Console.Cli;

namespace Gmail.Console.Infrastructure;

/// <summary>
/// Every command funnels its failures through one place, so the error envelope and the exit
/// code always agree. Returning null from <see cref="RunAsync"/> means "no stdout payload".
/// </summary>
public abstract class GmailCommand<TSettings> : AsyncCommand<TSettings> where TSettings : GlobalSettings
{
    protected sealed override async Task<int> ExecuteAsync(
        CommandContext context, TSettings settings, CancellationToken cancellation)
    {
        try
        {
            var result = await RunAsync(context, settings, cancellation);
            if (result is not null) OutputHelper.Write(result, settings.Format);
            return 0;
        }
        catch (GmailException ex)
        {
            OutputHelper.WriteError(ex, settings.ErrorFormat);
            return (int)ex.Code;
        }
        catch (HttpRequestException ex)
        {
            OutputHelper.WriteError(
                new GmailException(ErrorCode.Network, "Could not reach Google.", ex.Message), settings.ErrorFormat);
            return (int)ErrorCode.Network;
        }
        catch (TaskCanceledException)
        {
            OutputHelper.WriteError(
                new GmailException(ErrorCode.Network, $"The request timed out after {settings.TimeoutSeconds}s.",
                    null, "Retry, or raise --timeout."), settings.ErrorFormat);
            return (int)ErrorCode.Network;
        }
        catch (Exception ex)
        {
            OutputHelper.WriteError(
                new GmailException(ErrorCode.Error, ex.Message, ex.GetType().Name), settings.ErrorFormat);
            return (int)ErrorCode.Error;
        }
    }

    protected abstract Task<object?> RunAsync(CommandContext context, TSettings settings, CancellationToken ct);
}

/// <summary>A command that operates on one mailbox: resolves the account and opens a client.</summary>
public abstract class MailboxCommand<TSettings> : GmailCommand<TSettings> where TSettings : AccountSettings
{
    protected sealed override async Task<object?> RunAsync(
        CommandContext context, TSettings settings, CancellationToken ct)
    {
        var store = SecretStoreFactory.Create();
        var account = AccountResolver.Resolve(settings.Account);

        using var client = new GmailApiClient(account, store, settings.Verbose, settings.TimeoutSeconds);
        return await RunAsync(client, settings, ct);
    }

    protected abstract Task<object?> RunAsync(GmailApiClient client, TSettings settings, CancellationToken ct);
}
