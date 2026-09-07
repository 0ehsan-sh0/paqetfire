using System.Linq;
using System.Text.Json;
using PaqetFire.Broker.Network;
using PaqetFire.Core.Configuration;
using Xunit;

namespace PaqetFire.Core.Tests;

public sealed class HotspotSharingTests
{
    [Fact]
    public void HotspotShare_EmitsSecondInboundOnHotspotAddress()
    {
        var json = new XrayJsonConfigurationWriter().Write(new XrayRoutingPolicy(
            RegionalRoutingPreset.IranDirect,
            XrayDomainStrategy.IPIfNonMatch,
            BypassLan: true, BlockAds: true, BlockQuic: false, DirectBitTorrent: true,
            LanShare: new LanSocksShare("192.168.1.10", 1082, "paqetfire", "correct-horse-1"),
            HotspotShare: new LanSocksShare("192.168.137.1", 10808, "paqetfire", "correct-horse-1")));

        using var document = JsonDocument.Parse(json);
        var inbounds = document.RootElement.GetProperty("inbounds").EnumerateArray().ToArray();
        Assert.Equal(3, inbounds.Length);
        var hotspot = inbounds.First(i => i.GetProperty("tag").GetString() == "hotspot-share-in");
        Assert.Equal("192.168.137.1", hotspot.GetProperty("listen").GetString());
        Assert.Equal(10808, hotspot.GetProperty("port").GetInt32());
        Assert.Equal("password", hotspot.GetProperty("settings").GetProperty("auth").GetString());
        Assert.True(hotspot.GetProperty("settings").GetProperty("udp").GetBoolean());
        Assert.False(hotspot.GetProperty("sniffing").GetProperty("routeOnly").GetBoolean());
    }

    [Fact]
    public void HotspotOnly_RejectsCredentialsOutsideLanRules()
    {
        static PaqetFireSettings HotspotOnly(string username, string password) => new()
        {
            ServerEndpoint = "example.com:8443",
            TransportKey = "secret",
            ShareViaHotspot = true,
            HotspotSocksPort = 10808,
            LanSocksUsername = username,
            LanSocksPassword = password,
        };

        var cases = new[]
        {
            HotspotOnly(new string('u', 100), "correct-horse-1"),
            HotspotOnly("bad\tuser", "correct-horse-1"),
            HotspotOnly("paqetfire", new string('p', 129)),
            HotspotOnly("paqetfire", "bad\tpassword"),
        };

        foreach (var settings in cases)
        {
            Assert.Contains(
                PaqetFireSettingsValidator.Validate(settings),
                error => error.Contains("Hotspot sharing reuses the LAN share username and password", StringComparison.Ordinal));
        }
    }

    [Theory]
    [InlineData("192.168.137.1", true)]
    [InlineData("192.168.173.5", true)]
    [InlineData("192.168.1.10", false)]
    [InlineData("10.0.0.5", false)]
    public void IsHotspotAddress_MatchesOnlyIcsDefaults(string ip, bool expected)
    {
        Assert.Equal(expected, HotspotNetworkDetector.IsHotspotAddress(System.Net.IPAddress.Parse(ip)));
    }
}
