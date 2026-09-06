using PaqetFire.Core.Configuration;

namespace PaqetFire.Broker.Configuration;

public interface IMachineSettingsStore
{
    ValueTask<PaqetFireSettings?> LoadAsync(CancellationToken cancellationToken);

    ValueTask SaveAsync(PaqetFireSettings settings, CancellationToken cancellationToken);
}
