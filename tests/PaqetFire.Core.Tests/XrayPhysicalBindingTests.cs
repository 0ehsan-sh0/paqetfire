using System.Text.Json;
using PaqetFire.Core.Configuration;
using Xunit;

namespace PaqetFire.Core.Tests;

public sealed class XrayPhysicalBindingTests
{
    [Fact]
    public void DirectOutbound_WithPhysicalAdapterIp_EmitsSendThrough()
    {
        var policy = new XrayRoutingPolicy(
            RegionalPreset: RegionalRoutingPreset.IranDirect,
            DomainStrategy: XrayDomainStrategy.IPIfNonMatch,
            BypassLan: true,
            BlockAds: false,
            BlockQuic: false,
            DirectBitTorrent: false,
            PhysicalAdapterIp: "192.168.1.10");

        var json = new XrayJsonConfigurationWriter().Write(policy);
        using var doc = JsonDocument.Parse(json);
        var outbounds = doc.RootElement.GetProperty("outbounds");

        JsonElement? directOutbound = null;
        foreach (var outbound in outbounds.EnumerateArray())
        {
            if (outbound.GetProperty("tag").GetString() == "direct")
            {
                directOutbound = outbound;
                break;
            }
        }

        Assert.NotNull(directOutbound);
        Assert.Equal("192.168.1.10", directOutbound.Value.GetProperty("sendThrough").GetString());
    }

    [Fact]
    public void DirectOutbound_WithoutPhysicalAdapterIp_OmitsSendThrough()
    {
        var policy = new XrayRoutingPolicy(
            RegionalPreset: RegionalRoutingPreset.IranDirect,
            DomainStrategy: XrayDomainStrategy.IPIfNonMatch,
            BypassLan: true,
            BlockAds: false,
            BlockQuic: false,
            DirectBitTorrent: false,
            PhysicalAdapterIp: null);

        var json = new XrayJsonConfigurationWriter().Write(policy);
        using var doc = JsonDocument.Parse(json);
        var outbounds = doc.RootElement.GetProperty("outbounds");

        JsonElement? directOutbound = null;
        foreach (var outbound in outbounds.EnumerateArray())
        {
            if (outbound.GetProperty("tag").GetString() == "direct")
            {
                directOutbound = outbound;
                break;
            }
        }

        Assert.NotNull(directOutbound);
        Assert.False(directOutbound.Value.TryGetProperty("sendThrough", out _));
    }
}
