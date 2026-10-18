using System.Xml.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using PaqetFire.Broker.Deployment;
using PaqetFire.Broker.Runtime;
using Xunit;

namespace PaqetFire.Broker.Tests;

public sealed class EngineFirewallRulesTests
{
    private static readonly RuntimePaths Paths = new(
        @"C:\PF\payload",
        @"C:\PF\paqet.exe",
        @"C:\PF\paqet.yaml",
        @"C:\PF\xray\xray.exe",
        @"C:\PF\xray\config.json",
        @"C:\PF\xray\geoip.dat",
        @"C:\PF\xray\geosite.dat",
        @"C:\PF\proxifyre\ProxiFyre.exe",
        @"C:\PF\proxifyre\app-config.json",
        @"C:\PF\settings.json");

    [Fact]
    public void RepairRestoresEveryMissingRule()
    {
        var store = new InMemoryFirewallRuleStore();

        CreateRules(store).Repair();

        Assert.Equal(EngineFirewallRules.Required(Paths), store.Rules);
        Assert.Equal(4, store.ReplaceCount);
    }

    [Fact]
    public void RepairLeavesMatchingRulesUntouched()
    {
        var store = new InMemoryFirewallRuleStore();
        store.Rules.AddRange(EngineFirewallRules.Required(Paths).Select(rule => rule with
        {
            ProgramPath = rule.ProgramPath.ToLowerInvariant(),
            Description = "edited",
        }));

        CreateRules(store).Repair();

        Assert.Equal(0, store.ReplaceCount);
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("blocking")]
    [InlineData("stale-path")]
    [InlineData("wrong-scope")]
    [InlineData("duplicate")]
    public void RepairReplacesAlteredRule(string alteration)
    {
        var required = EngineFirewallRules.Required(Paths);
        var proxiFyreTcp = required.Single(rule => rule.Name == "PaqetFire ProxiFyre TCP");
        var store = new InMemoryFirewallRuleStore();
        store.Rules.AddRange(required.Where(rule => rule != proxiFyreTcp));
        store.Rules.Add(alteration switch
        {
            "disabled" => proxiFyreTcp with { IsEnabled = false },
            "blocking" => proxiFyreTcp with { IsInboundAllowOnAllProfiles = false },
            "stale-path" => proxiFyreTcp with { ProgramPath = @"C:\Old\ProxiFyre.exe" },
            "wrong-scope" => proxiFyreTcp with { RemoteAddresses = "LocalSubnet" },
            _ => proxiFyreTcp,
        });
        if (alteration == "duplicate")
        {
            store.Rules.Add(proxiFyreTcp);
        }

        CreateRules(store).Repair();

        Assert.Equal(1, store.ReplaceCount);
        Assert.Equal(proxiFyreTcp, Assert.Single(store.FindByName(proxiFyreTcp.Name)));
    }

    [Fact]
    public void RepairContinuesWhenTheFirewallRejectsOneRule()
    {
        var store = new InMemoryFirewallRuleStore { RejectedName = "PaqetFire LAN SOCKS5 TCP" };

        CreateRules(store).Repair();

        Assert.Equal(3, store.Rules.Count);
    }

    [Fact]
    public void RequiredRulesMatchTheInstallerExceptions()
    {
        var firewall = XNamespace.Get("http://wixtoolset.org/schemas/v4/wxs/firewall");
        var installer = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "installer-package.wxs"))
            .Descendants(firewall + "FirewallException")
            .Select(exception => (
                Name: (string)exception.Attribute("Name")!,
                Protocol: (string)exception.Attribute("Protocol")!,
                Scope: (string)exception.Attribute("Scope")!,
                Profile: (string)exception.Attribute("Profile")!))
            .OrderBy(exception => exception.Name);
        var broker = EngineFirewallRules.Required(Paths)
            .Select(rule => (
                rule.Name,
                Protocol: rule.Protocol.ToString().ToLowerInvariant(),
                Scope: rule.RemoteAddresses == "*" ? "any" : "localSubnet",
                Profile: "all"))
            .OrderBy(rule => rule.Name);

        Assert.Equal(installer, broker);
    }

    private static EngineFirewallRules CreateRules(IFirewallRuleStore store) =>
        new(store, Paths, NullLogger<EngineFirewallRules>.Instance);
}

internal sealed class InMemoryFirewallRuleStore : IFirewallRuleStore
{
    public List<FirewallRule> Rules { get; } = [];

    public int ReplaceCount { get; private set; }

    public string? RejectedName { get; init; }

    public IReadOnlyList<FirewallRule> FindByName(string name) =>
        Rules.Where(rule => string.Equals(rule.Name, name, StringComparison.OrdinalIgnoreCase)).ToArray();

    public void Replace(FirewallRule rule)
    {
        if (rule.Name == RejectedName)
        {
            throw new UnauthorizedAccessException("Local firewall rules are disabled by policy.");
        }

        Rules.RemoveAll(existing => string.Equals(existing.Name, rule.Name, StringComparison.OrdinalIgnoreCase));
        Rules.Add(rule);
        ReplaceCount++;
    }
}
