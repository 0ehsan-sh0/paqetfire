namespace PaqetFire.Core.Configuration;

/// <summary>
/// Configuration for chaining non-Iran traffic through an upstream OpenVPN tunnel.
/// </summary>
public sealed record UpstreamOpenVpnOptions
{
    public bool Enabled { get; init; }

    public string? OvpnConfigText { get; init; }

    public string? Username { get; init; }

    public string? Password { get; init; }

    public string? RemoteHost { get; init; }

    public int? RemotePort { get; init; }

    public string? Protocol { get; init; }
}
