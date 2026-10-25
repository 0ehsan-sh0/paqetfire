using PaqetFire.Broker.Configuration;
using PaqetFire.Core.Configuration;
using Xunit;

namespace PaqetFire.Broker.Tests;

public sealed class MachineSettingsStoreTests
{
    [BrokerIdentityFact]
    public async Task SaveAsync_PreservesUpstreamOpenVpnPassword()
    {
        var directory = Path.Combine(Path.GetTempPath(), "PaqetFire-settings-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "settings.json");
        try
        {
            var store = new MachineSettingsStore(path);
            var original = new PaqetFireSettings
            {
                ServerEndpoint = "51.254.208.73:8443",
                TransportKey = "test-key",
                UpstreamOpenVpn = new UpstreamOpenVpnOptions
                {
                    Enabled = true,
                    Username = "proton-user",
                    Password = "secret-vpn-password",
                    OvpnConfigText = "client\ndev tun\nremote 1.2.3.4 1194 udp"
                }
            };

            await store.SaveAsync(original, CancellationToken.None);
            var loaded = await store.LoadAsync(CancellationToken.None);

            Assert.NotNull(loaded);
            Assert.Equal("secret-vpn-password", loaded.UpstreamOpenVpn.Password);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                foreach (var file in Directory.GetFiles(directory)) File.Delete(file);
                Directory.Delete(directory);
            }
        }
    }
}
