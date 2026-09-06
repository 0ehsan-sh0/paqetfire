using PaqetFire.Core.Configuration;

namespace PaqetFire.Core.Routing;

public sealed record ProxiFyreRoutePlan(
    IReadOnlyList<ProxyRule> Rules,
    IReadOnlyList<string> Exclusions,
    bool BypassLan,
    ProxiFyreLogLevel LogLevel = ProxiFyreLogLevel.Info);
