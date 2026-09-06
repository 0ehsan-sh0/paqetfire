using PaqetFire.Core.Engines;

namespace PaqetFire.Core.Connections;

public sealed record ConnectionStatus(
    ConnectionState State,
    EngineStatus Paqet,
    EngineStatus Xray,
    EngineStatus ProxiFyre,
    string? Detail = null,
    DateTimeOffset? ObservedAt = null);
