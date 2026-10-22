using PaqetFire.Core.Engines;

namespace PaqetFire.Core.Connections;

public enum ConnectionRecoveryAction
{
    None,
    /// <summary>Only Paqet is down; Xray and ProxiFyre keep routed traffic failing closed.</summary>
    RestartTransport,
    SafeRecovery,
}

public static class ConnectionRecoveryPolicy
{
    public static ConnectionRecoveryAction Evaluate(
        ConnectionState connectionState,
        bool isKillSwitchEnabled,
        IReadOnlyList<EngineStatus> engines)
    {
        if (connectionState is ConnectionState.Connecting or ConnectionState.Disconnecting)
        {
            return ConnectionRecoveryAction.None;
        }

        if (IsTransportOnlyFault(engines))
        {
            return ConnectionRecoveryAction.RestartTransport;
        }

        var paqet = Find(engines, EngineKind.Paqet);
        var xray = Find(engines, EngineKind.Xray);
        var proxiFyre = Find(engines, EngineKind.ProxiFyre);
        var allHealthy = paqet?.State == EngineState.Running &&
                         xray?.State == EngineState.Running &&
                         proxiFyre?.State == EngineState.Running;
        var guarded = isKillSwitchEnabled &&
                      paqet?.State == EngineState.Stopped &&
                      xray?.State == EngineState.Stopped &&
                      proxiFyre?.State == EngineState.Running;
        var anyEngineActive = engines.Any(engine =>
            engine.State is EngineState.Running or EngineState.Starting or EngineState.Faulted);

        return !allHealthy && !guarded && anyEngineActive
            ? ConnectionRecoveryAction.SafeRecovery
            : ConnectionRecoveryAction.None;
    }

    /// <summary>
    /// Whether the chain must be released to the configured safe state. A fault of the
    /// carrier alone is excluded: the broker restarts Paqet in place for it.
    /// </summary>
    public static bool RequiresSafeRecovery(
        ConnectionState connectionState,
        bool isKillSwitchEnabled,
        IReadOnlyList<EngineStatus> engines) =>
        Evaluate(connectionState, isKillSwitchEnabled, engines) == ConnectionRecoveryAction.SafeRecovery;

    public static bool IsTransportOnlyFault(IReadOnlyList<EngineStatus> engines) =>
        Find(engines, EngineKind.Paqet)?.State is EngineState.Faulted or EngineState.Stopped &&
        Find(engines, EngineKind.Xray)?.State == EngineState.Running &&
        Find(engines, EngineKind.ProxiFyre)?.State == EngineState.Running;

    private static EngineStatus? Find(IReadOnlyList<EngineStatus> engines, EngineKind kind) =>
        engines.FirstOrDefault(engine => engine.Engine == kind);
}
