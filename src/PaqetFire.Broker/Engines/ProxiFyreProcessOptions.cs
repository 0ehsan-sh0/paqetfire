namespace PaqetFire.Broker.Engines;

public sealed record ProxiFyreProcessOptions
{
    public required string ExecutablePath { get; init; }

    public required string ConfigurationPath { get; init; }

    public string? Version { get; init; }

    public string? ExpectedExecutableSha256 { get; init; }

    public TimeSpan ReadinessTimeout { get; init; } = TimeSpan.FromSeconds(8);

    public TimeSpan ProbeInterval { get; init; } = TimeSpan.FromMilliseconds(150);

    public int MaximumLogEntries { get; init; } = 128;

    public int MaximumLogLineLength { get; init; } = 4096;
}
