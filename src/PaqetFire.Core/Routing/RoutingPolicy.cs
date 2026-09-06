using PaqetFire.Core.Configuration;

namespace PaqetFire.Core.Routing;

public sealed record RoutingPolicy(
    RoutingMode Mode,
    string LocalSocksEndpoint,
    IReadOnlyList<string> SelectedApplications,
    IReadOnlyList<string> UserExclusions,
    bool BypassLan = false,
    bool RouteTcp = true,
    bool RouteUdp = true,
    bool RouteIpv4 = true,
    bool RouteIpv6 = true,
    ProxiFyreLogLevel LogLevel = ProxiFyreLogLevel.Info,
    string? Username = null,
    string? Password = null,
    Socks5Transport Transport = Socks5Transport.Tcp,
    string? TlsServerName = null,
    string? TlsPinnedSha256 = null,
    bool TlsAllowInvalidCertificate = false);
