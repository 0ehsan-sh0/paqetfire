namespace PaqetFire.Broker.Engines;

public sealed record PaqetProcessLogEntry(
    DateTimeOffset Timestamp,
    bool IsError,
    string Message)
{
    // Runtime snapshots use the domain-wide timestamp name.
    public DateTimeOffset OccurredAt => Timestamp;
}
