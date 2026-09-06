namespace PaqetFire.Broker.Deployment;

public enum PayloadInspectionState
{
    Ready,
    NotStaged,
    Invalid,
}

public sealed record PayloadInspection(
    PayloadInspectionState State,
    string Detail,
    IReadOnlyDictionary<string, string>? EngineVersions = null);
