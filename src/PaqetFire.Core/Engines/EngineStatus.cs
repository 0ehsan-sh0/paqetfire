namespace PaqetFire.Core.Engines;

public sealed record EngineStatus(
    EngineKind Engine,
    EngineState State,
    string? Version = null,
    string? Endpoint = null,
    string? Detail = null,
    DateTimeOffset? ChangedAt = null);
