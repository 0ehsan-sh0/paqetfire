namespace PaqetFire.Core.Connections;

public interface IConnectionController
{
    ValueTask<ConnectionStatus> GetStatusAsync(CancellationToken cancellationToken);

    ValueTask ConnectAsync(CancellationToken cancellationToken);

    ValueTask GuardAsync(CancellationToken cancellationToken);

    ValueTask DisconnectAsync(CancellationToken cancellationToken);

    /// <summary>Restarts only Paqet; Xray and ProxiFyre keep capturing, so traffic fails closed meanwhile.</summary>
    ValueTask RestartTransportAsync(CancellationToken cancellationToken);
}
