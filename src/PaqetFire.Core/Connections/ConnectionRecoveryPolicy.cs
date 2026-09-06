using PaqetFire.Core.Engines;

namespace PaqetFire.Core.Connections;

public static class ConnectionRecoveryPolicy
{
    public static bool RequiresSafeRecovery(
        ConnectionState connectionState,
        bool isKillSwitchEnabled,
        IReadOnlyList<EngineStatus> engines)
    {
        if (connectionState is ConnectionState.Connecting or ConnectionState.Disconnecting)
        {
            return false;
        }

        var paqet = engines.FirstOrDefault(engine => engine.Engine == EngineKind.Paqet);
        var xray = engines.FirstOrDefault(engine => engine.Engine == EngineKind.Xray);
        var proxiFyre = engines.FirstOrDefault(engine => engine.Engine == EngineKind.ProxiFyre);
        var allHealthy = paqet?.State == EngineState.Running &&
                         xray?.State == EngineState.Running &&
                         proxiFyre?.State == EngineState.Running;
        var guarded = isKillSwitchEnabled &&
                      paqet?.State == EngineState.Stopped &&
                      xray?.State == EngineState.Stopped &&
                      proxiFyre?.State == EngineState.Running;
        var anyEngineActive = engines.Any(engine =>
            engine.State is EngineState.Running or EngineState.Starting or EngineState.Faulted);

        return !allHealthy && !guarded && anyEngineActive;
    }
}
