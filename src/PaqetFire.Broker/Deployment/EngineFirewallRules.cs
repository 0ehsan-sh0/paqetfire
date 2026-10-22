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

    /// <summary>Looks up several names at once; names without rules may be absent from the result.</summary>
    IReadOnlyDictionary<string, IReadOnlyList<FirewallRule>> FindByNames(IReadOnlyCollection<string> names) =>
        names.Distinct(StringComparer.OrdinalIgnoreCase)
            .ToDictionary(name => name, FindByName, StringComparer.OrdinalIgnoreCase);

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
    ILogger<EngineFirewallRules> logger,
    TimeProvider? timeProvider = null)
{
    // Reading the rule collection is slow COM work; a recent clean check is trusted for a
    // few minutes so reconnects and recoveries do not repeat it.
    public static readonly TimeSpan HealthyCheckLifetime = TimeSpan.FromMinutes(5);

    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly Lock sync = new();
    private Task? running;
    private DateTimeOffset? healthyAt;

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

    /// <summary>
    /// Starts a repair in the background unless one is running or a recent check found
    /// every rule intact. Await the result only where the rules are needed: before ProxiFyre starts.
    /// </summary>
    public Task EnsureRepairedAsync()
    {
        lock (sync)
        {
            if (running is { IsCompleted: false })
            {
                return running;
            }

            if (healthyAt is { } checkedAt && clock.GetUtcNow() - checkedAt < HealthyCheckLifetime)
            {
                return Task.CompletedTask;
            }

            return running = Task.Run(() =>
            {
                var healthy = Repair();
                lock (sync)
                {
                    healthyAt = healthy ? clock.GetUtcNow() : null;
                }
            });
        }
    }

    /// <returns>True when every rule was already intact or has been restored.</returns>
    public bool Repair()
    {
        var required = Required(paths);
        IReadOnlyDictionary<string, IReadOnlyList<FirewallRule>> existingRules;
        try
        {
            existingRules = store.FindByNames(required.Select(rule => rule.Name).ToArray());
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "The Windows Firewall rules could not be read.");
            return false;
        }

        var healthy = true;
        foreach (var rule in required)
        {
            try
            {
                var existing = existingRules.GetValueOrDefault(rule.Name) ?? [];
                if (existing.Count == 1 && Satisfies(existing[0], rule))
                {
                    continue;
                }

                store.Replace(rule);
                logger.LogWarning("Restored the missing or modified Windows Firewall rule '{Rule}'.", rule.Name);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                healthy = false;
                logger.LogWarning(exception, "The Windows Firewall rule '{Rule}' could not be restored.", rule.Name);
            }
        }

        return healthy;
    }

    private static bool Satisfies(FirewallRule actual, FirewallRule required) =>
        actual.IsEnabled &&
        actual.IsInboundAllowOnAllProfiles &&
        actual.Protocol == required.Protocol &&
        string.Equals(actual.ProgramPath, required.ProgramPath, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(actual.RemoteAddresses, required.RemoteAddresses, StringComparison.OrdinalIgnoreCase);
}
