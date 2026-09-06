using PaqetFire.Core.Ipc;
using PaqetFire.Core.Configuration;

namespace PaqetFire.Desktop.Ipc;

public interface IBrokerClient
{
    event Action<BrokerEvent>? EventReceived;

    Task OpenAsync(TimeSpan timeout, CancellationToken cancellationToken);

    Task CloseAsync(CancellationToken cancellationToken);

    ValueTask<BrokerSnapshot> GetSnapshotAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken);

    ValueTask<BrokerSnapshot> SaveSettingsAsync(
        PaqetFireSettings settings,
        bool connectAfterSave,
        TimeSpan timeout,
        CancellationToken cancellationToken);

    ValueTask<BrokerSnapshot> SetConnectionStateAsync(
        bool connected,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}
