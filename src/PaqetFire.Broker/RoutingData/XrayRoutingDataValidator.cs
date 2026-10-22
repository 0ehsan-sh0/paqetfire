using System.Text.Json;
using PaqetFire.Broker.Engines;
using PaqetFire.Core.RoutingData;

namespace PaqetFire.Broker.RoutingData;

public interface IRoutingDataValidator
{
    /// <summary>Fails unless Xray loads every category PaqetFire references from <paramref name="assetDirectory"/>.</summary>
    Task ValidateAsync(string assetDirectory, CancellationToken cancellationToken);
}

/// <summary>Runs the pinned <c>xray run -test</c> against a probe configuration that names every required category.</summary>
public sealed class XrayRoutingDataValidator(XrayProcessAdapter xray) : IRoutingDataValidator
{
    private const string ProbeFileName = "probe.json";

    public async Task ValidateAsync(string assetDirectory, CancellationToken cancellationToken)
    {
        var probe = Path.Combine(assetDirectory, ProbeFileName);
        await File.WriteAllTextAsync(probe, CreateProbeConfiguration(), cancellationToken).ConfigureAwait(false);
        try
        {
            await xray.ValidateRoutingDataAsync(assetDirectory, probe, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            File.Delete(probe);
        }
    }

    internal static string CreateProbeConfiguration() => JsonSerializer.Serialize(new
    {
        log = new { loglevel = "warning" },
        outbounds = new object[]
        {
            new { tag = "direct", protocol = "freedom" },
            new { tag = "block", protocol = "blackhole" },
        },
        routing = new
        {
            domainStrategy = "AsIs",
            rules = new object[]
            {
                new { type = "field", domain = RoutingDataRelease.RequiredSiteCategories, outboundTag = "block" },
                new { type = "field", ip = RoutingDataRelease.RequiredIpCategories, outboundTag = "block" },
            },
        },
    });
}
