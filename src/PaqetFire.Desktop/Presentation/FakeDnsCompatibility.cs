using PaqetFire.Core.Configuration;

namespace PaqetFire.Desktop.Presentation;

/// <summary>Mirrors the broker's FakeDNS rules so the form can switch it off instead of failing on save.</summary>
public static class FakeDnsCompatibility
{
    public static string? GetConflict(
        bool routeAllApplications,
        bool hasUserExclusions,
        XrayDnsResolver resolver,
        bool routeIpv4)
    {
        if (resolver == XrayDnsResolver.System)
        {
            return "FakeDNS is off because the system resolver is selected; it needs encrypted DNS.";
        }

        if (!routeIpv4)
        {
            return "FakeDNS is off because IPv4 routing is off; its placeholder addresses are IPv4.";
        }

        // Windows caches DNS answers for every process, so an excluded application would
        // receive placeholder addresses that only work inside the route.
        if (routeAllApplications && hasUserExclusions)
        {
            return "FakeDNS is off because additional exclusions are set in all-applications mode. Remove the exclusions to use it.";
        }

        return null;
    }
}
