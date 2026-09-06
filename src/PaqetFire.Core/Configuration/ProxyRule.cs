namespace PaqetFire.Core.Configuration;

public sealed record ProxyRule(
    string Name,
    IReadOnlyList<string> Applications,
    string Socks5Endpoint,
    bool RouteTcp = true,
    bool RouteUdp = true,
    bool RouteIpv4 = true,
    bool RouteIpv6 = true,
    string? Username = null,
    string? Password = null,
    Socks5Transport Transport = Socks5Transport.Tcp,
    string? TlsServerName = null,
    string? TlsPinnedSha256 = null,
    bool TlsAllowInvalidCertificate = false);
