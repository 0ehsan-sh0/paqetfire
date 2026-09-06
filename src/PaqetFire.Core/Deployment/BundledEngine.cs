using PaqetFire.Core.Engines;

namespace PaqetFire.Core.Deployment;

public sealed record BundledEngine(
    EngineKind Engine,
    string Version,
    string EntryPoint,
    IReadOnlyList<PayloadFile> Files,
    string? ServiceName = null);
