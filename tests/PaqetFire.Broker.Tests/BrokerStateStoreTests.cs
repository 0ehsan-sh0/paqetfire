using PaqetFire.Broker.Configuration;
using Xunit;

namespace PaqetFire.Broker.Tests;

public sealed class BrokerStateStoreTests
{
    [Fact]
    public void MissingOrCorruptStateMeansNoRouteToRestore()
    {
        var directory = Path.Combine(Path.GetTempPath(), "PaqetFire-state-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "broker-state.json");
        try
        {
            Assert.False(new BrokerStateStore(path).Load().DesiredConnected);
            File.WriteAllText(path, "not protected data");
            Assert.False(new BrokerStateStore(path).Load().DesiredConnected);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [ElevatedFact]
    public async Task StateSurvivesRestartsWithoutPlaintextServerAddresses()
    {
        var directory = Path.Combine(Path.GetTempPath(), "PaqetFire-state-" + Guid.NewGuid());
        var path = Path.Combine(directory, "broker-state.json");
        var profileId = Guid.NewGuid();
        try
        {
            await new BrokerStateStore(path).SaveAsync(new BrokerRuntimeState
            {
                DesiredConnected = true,
                DesiredProfileId = profileId,
                ResolvedServers = new Dictionary<string, string> { ["vpn.example.net"] = "198.51.100.4" },
                Ipv6Support = new Dictionary<string, bool> { ["vpn.example.net:8443"] = false },
            }, CancellationToken.None);

            var loaded = new BrokerStateStore(path).Load();

            Assert.True(loaded.DesiredConnected);
            Assert.Equal(profileId, loaded.DesiredProfileId);
            Assert.Equal("198.51.100.4", loaded.ResolvedServers["VPN.example.net"]);
            Assert.False(loaded.Ipv6Support["vpn.example.net:8443"]);
            Assert.DoesNotContain("198.51.100.4", File.ReadAllText(path), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
