using PaqetFire.Core.Configuration;
using PaqetFire.Core.Connections;
using PaqetFire.Core.Engines;
using Xunit;

namespace PaqetFire.Broker.Tests;

public sealed class RuntimeOpenVpnTests
{
    [Fact]
    public async Task ConnectAsync_WhenUpstreamOpenVpnEnabled_ConfiguresOpenVpnAndXrayPolicy()
    {
        var ovpnConfig = "client\ndev tun\nproto udp\nremote 1.2.3.4 1194\nresolv-retry infinite\nnobind\n";
        await using var fixture = await RuntimeFixture.StartAsync(settings => settings with
        {
            UpstreamOpenVpn = new UpstreamOpenVpnOptions
            {
                Enabled = true,
                OvpnConfigText = ovpnConfig,
                Username = "proton_test_user",
                Password = "proton_test_password",
            }
        });

        var snapshot = await fixture.Runtime.ConnectAsync(CancellationToken.None);

        Assert.True(fixture.Controller.IsOpenVpnEnabled);
        Assert.NotNull(fixture.Xray.Last);
        Assert.True(fixture.Xray.Last.IsOpenVpnActive);
        Assert.True(File.Exists(fixture.Paths.ResolvedOpenVpnConfigPath));
        Assert.True(File.Exists(fixture.Paths.ResolvedOpenVpnAuthPath));
        Assert.False(File.Exists(fixture.Paths.ResolvedOpenVpnSocksAuthPath));
        Assert.Contains(snapshot.Engines, e => e.Engine == EngineKind.OpenVpn);
        Assert.Equal(4, snapshot.Engines.Count);

        var runtimeOvpn = await File.ReadAllTextAsync(fixture.Paths.ResolvedOpenVpnConfigPath);
        Assert.Contains("socks-proxy 127.0.0.1 1080", runtimeOvpn);
        Assert.DoesNotContain("socks-auth.txt", runtimeOvpn);
        Assert.Contains("auth-user-pass", runtimeOvpn);
        Assert.DoesNotContain("windows-driver", runtimeOvpn);
        Assert.Contains("dev tun", runtimeOvpn);
        Assert.DoesNotContain(@"\", runtimeOvpn.Substring(runtimeOvpn.IndexOf("socks-proxy")));

        var protonAuth = await File.ReadAllTextAsync(fixture.Paths.ResolvedOpenVpnAuthPath);
        Assert.Contains("proton_test_user", protonAuth);
        Assert.Contains("proton_test_password", protonAuth);
    }

    [Fact]
    public async Task ConnectAsync_WhenUpstreamOpenVpnDisabled_LeavesOpenVpnInactive()
    {
        await using var fixture = await RuntimeFixture.StartAsync(settings => settings with
        {
            UpstreamOpenVpn = new UpstreamOpenVpnOptions
            {
                Enabled = false,
                OvpnConfigText = "client\n",
            }
        });

        var snapshot = await fixture.Runtime.ConnectAsync(CancellationToken.None);

        Assert.False(fixture.Controller.IsOpenVpnEnabled);
        Assert.NotNull(fixture.Xray.Last);
        Assert.False(fixture.Xray.Last.IsOpenVpnActive);
        Assert.Equal(ConnectionState.Connected, snapshot.ConnectionState);
        Assert.Equal(3, snapshot.Engines.Count);
        Assert.DoesNotContain(snapshot.Engines, e => e.Engine == EngineKind.OpenVpn);
    }
}
