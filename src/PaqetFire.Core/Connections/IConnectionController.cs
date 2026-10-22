using PaqetFire.Core.Ipc;

namespace PaqetFire.Core.Connections;

public interface IConnectionController
{
    ValueTask<ConnectionStatus> GetStatusAsync(CancellationToken cancellationToken);

    ValueTask ConnectAsync(CancellationToken cancellationToken);

    /// <summary>Connects and reports each startup stage as it begins.</summary>
    ValueTask ConnectAsync(Action<ConnectionStage>? stageStarted, CancellationToken cancellationToken) =>
        ConnectAsync(cancellationToken);

    ValueTask GuardAsync(CancellationToken cancellationToken);

    ValueTask DisconnectAsync(CancellationToken cancellationToken);

    /// <summary>Restarts only Paqet; Xray and ProxiFyre keep capturing, so traffic fails closed meanwhile.</summary>
    ValueTask RestartTransportAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Restarts Paqet and, when <paramref name="includePolicy"/> is set, Xray as well.
    /// ProxiFyre keeps capturing either way.
    /// </summary>
    ValueTask RestartTransportAsync(bool includePolicy, CancellationToken cancellationToken);
}
