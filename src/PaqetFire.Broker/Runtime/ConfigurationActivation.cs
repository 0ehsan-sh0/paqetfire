using PaqetFire.Core.Connections;
using PaqetFire.Core.Ipc;

namespace PaqetFire.Broker.Runtime;

public static class ConfigurationActivation
{
    // Keep the current route/guard running until every configuration and settings write succeeds.
    // ProxiFyre must then restart to read its new rules; this is not a machine-wide no-gap guard.
    public static async Task<Exception?> ApplyAsync(
        IConnectionController controller,
        Func<Task> persist,
        bool stopCurrentRoute,
        bool enableGuard,
        bool reconnect,
        CancellationToken cancellationToken,
        Action<ConnectionStage>? stageStarted = null)
    {
        await persist().ConfigureAwait(false);
        try
        {
            if (stopCurrentRoute)
                await controller.DisconnectAsync(cancellationToken).ConfigureAwait(false);
            if (enableGuard)
                await controller.GuardAsync(cancellationToken).ConfigureAwait(false);
            if (reconnect)
                await controller.ConnectAsync(stageStarted, cancellationToken).ConfigureAwait(false);
            return null;
        }
        catch (Exception error)
        {
            if (enableGuard)
            {
                try
                {
                    await controller.GuardAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception guardError)
                {
                    throw new AggregateException("Configuration was saved, but activation and restoring the routing guard failed.", error, guardError);
                }
            }
            if (error is OperationCanceledException) throw;
            return error;
        }
    }
}
