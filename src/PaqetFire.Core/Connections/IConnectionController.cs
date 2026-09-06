namespace PaqetFire.Core.Connections;

public interface IConnectionController
{
    ValueTask<ConnectionStatus> GetStatusAsync(CancellationToken cancellationToken);

    ValueTask ConnectAsync(CancellationToken cancellationToken);

    ValueTask GuardAsync(CancellationToken cancellationToken);

    ValueTask DisconnectAsync(CancellationToken cancellationToken);
}
