using System.Net;

namespace PaqetFire.Broker.Engines;

public sealed record PaqetProcessOptions
{
    public required string ExecutablePath { get; init; }

    public required string ConfigurationPath { get; init; }

    public required string TrustedExecutableRoot { get; init; }

    public required string TrustedConfigurationRoot { get; init; }

    public required IPEndPoint SocksEndpoint { get; init; }

    public string? Version { get; init; }

    /// <summary>
    /// SHA-256 from the signed PaqetFire payload manifest. When supplied, the
    /// executable is checked again immediately before every launch.
    /// </summary>
    public string? ExpectedExecutableSha256 { get; init; }

    public TimeSpan ReadinessTimeout { get; init; } = TimeSpan.FromSeconds(20);

    public TimeSpan ShutdownTimeout { get; init; } = TimeSpan.FromSeconds(8);

    public TimeSpan ProbeInterval { get; init; } = TimeSpan.FromMilliseconds(200);

    public int MaximumLogEntries { get; init; } = 256;

    public int MaximumLogLineLength { get; init; } = 4096;
}
