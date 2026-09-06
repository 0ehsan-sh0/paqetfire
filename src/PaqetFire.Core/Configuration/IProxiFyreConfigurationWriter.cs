using PaqetFire.Core.Routing;

namespace PaqetFire.Core.Configuration;

public interface IProxiFyreConfigurationWriter
{
    string Write(ProxiFyreRoutePlan routePlan, IReadOnlyCollection<string> lockedExclusions);
}
