using PaqetFire.Broker.Runtime;

namespace PaqetFire.Broker.Deployment;

public enum FirewallProtocol
{
    Tcp = 6,
    Udp = 17,
}

public sealed record FirewallRule(
    string Name,
    string Description,
    string ProgramPath,
    FirewallProtocol Protocol,
    string RemoteAddresses,
    bool IsEnabled = true,
    bool IsInboundAllowOnAllProfiles = true);

public interface IFirewallRuleStore
{
    IReadOnlyList<FirewallRule> FindByName(string name);

    void Replace(FirewallRule rule);
}

/// <summary>
/// Restores the installer's engine firewall exceptions. Windows Installer removes them by name
/// whenever any PaqetFire package is uninstalled (including side-by-side test builds), and a
/// firewall reset removes them too. Without them, the Private/Public profile silently drops
/// ProxiFyre's redirected traffic because Windows treats it as inbound on the physical adapter.
/// </summary>
public sealed class EngineFirewallRules(
    IFirewallRuleStore store,
    RuntimePaths paths,
    ILogger<EngineFirewallRules> logger)
{
    // Names and scopes must stay identical to installer/PaqetFire.Installer/Package.wxs so that
    // uninstalling the MSI also removes rules the broker restored.
    public static IReadOnlyList<FirewallRule> Required(RuntimePaths paths) =>
    [
        new("PaqetFire LAN SOCKS5 TCP",
            "Allows authenticated PaqetFire SOCKS5 access from the local subnet.",
            paths.XrayExecutablePath, FirewallProtocol.Tcp, "LocalSubnet"),
        new("PaqetFire LAN SOCKS5 UDP",
            "Allows authenticated PaqetFire SOCKS5 UDP access from the local subnet.",
            paths.XrayExecutablePath, FirewallProtocol.Udp, "LocalSubnet"),
        new("PaqetFire ProxiFyre TCP",
            "Allows ProxiFyre local TCP redirect listeners.",
            paths.ProxiFyreExecutablePath, FirewallProtocol.Tcp, "*"),
        new("PaqetFire ProxiFyre UDP",
            "Allows ProxiFyre local UDP redirect listeners.",
            paths.ProxiFyreExecutablePath, FirewallProtocol.Udp, "*"),
    ];

    public void Repair()
    {
        foreach (var rule in Required(paths))
        {
            try
            {
                var existing = store.FindByName(rule.Name);
                if (existing.Count == 1 && Satisfies(existing[0], rule))
                {
                    continue;
                }

                store.Replace(rule);
                logger.LogWarning("Restored the missing or modified Windows Firewall rule '{Rule}'.", rule.Name);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "The Windows Firewall rule '{Rule}' could not be restored.", rule.Name);
            }
        }
    }

    private static bool Satisfies(FirewallRule actual, FirewallRule required) =>
        actual.IsEnabled &&
        actual.IsInboundAllowOnAllProfiles &&
        actual.Protocol == required.Protocol &&
        string.Equals(actual.ProgramPath, required.ProgramPath, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(actual.RemoteAddresses, required.RemoteAddresses, StringComparison.OrdinalIgnoreCase);
}
