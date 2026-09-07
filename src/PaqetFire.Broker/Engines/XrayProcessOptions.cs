using System.Net;

namespace PaqetFire.Broker.Engines;

public sealed record XrayProcessOptions
{
    public required string ExecutablePath { get; init; }

    public required string ConfigurationPath { get; init; }

    public required string GeoIpPath { get; init; }

    public required string GeoSitePath { get; init; }

    public required string ExpectedExecutableSha256 { get; init; }

    public required string Version { get; init; }

    public IPEndPoint InboundEndpoint { get; init; } = new(IPAddress.Loopback, 1081);

    public TimeSpan ReadinessTimeout { get; init; } = TimeSpan.FromSeconds(15);

    public TimeSpan ValidationTimeout { get; init; } = TimeSpan.FromSeconds(15);
}
