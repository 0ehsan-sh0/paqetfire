using System.Linq;
using System.Text.Json;
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
}
