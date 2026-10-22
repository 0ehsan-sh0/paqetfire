using System.Text.Json;
using PaqetFire.Core.Configuration;
using PaqetFire.Core.Profiles;
using PaqetFire.Core.RoutingData;
using Xunit;

namespace PaqetFire.Core.Tests;

public sealed class RoutingDataTests
{
    [Theory]
    [InlineData("202610220935", true)]
    [InlineData("202602291200", false)]
    [InlineData("202613010000", false)]
    [InlineData("20261022093", false)]
    [InlineData("v202610220935", false)]
    [InlineData("../202610220935", false)]
    [InlineData(null, false)]
    public void ReleaseTagsAreFixedWidthUtcTimestamps(string? tag, bool expected) =>
        Assert.Equal(expected, RoutingDataRelease.IsValidTag(tag));

    [Fact]
    public void TagsFarAheadOfTheClockAreRejected()
    {
        var now = new DateTimeOffset(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);
        Assert.True(RoutingDataRelease.IsValidTag("202610220935", now));
        // Up to a day of clock skew is tolerated.
        Assert.True(RoutingDataRelease.IsValidTag("202610231159", now));
        Assert.False(RoutingDataRelease.IsValidTag("202610231201", now));
        Assert.False(RoutingDataRelease.IsValidTag("999912312359", now));
        Assert.False(RoutingDataRelease.IsValidTag("latest", now));
    }

    [Fact]
    public void DownloadsAreCappedWellAboveTheCurrentFileSizes() =>
        Assert.Equal(32L * 1024 * 1024, RoutingDataRelease.MaximumFileBytes);

    [Fact]
    public void OnlyLaterValidTagsCountAsNewer()
    {
        Assert.True(RoutingDataRelease.IsNewer("202610220935", null));
        Assert.True(RoutingDataRelease.IsNewer("202610220935", "202610210934"));
        Assert.False(RoutingDataRelease.IsNewer("202610210934", "202610220935"));
        Assert.False(RoutingDataRelease.IsNewer("202610220935", "202610220935"));
        Assert.False(RoutingDataRelease.IsNewer("latest", null));
    }

    [Fact]
    public void AssetUrisArePinnedToTheReleaseAndKnownFiles()
    {
        Assert.Equal(
            "https://github.com/Chocolate4U/Iran-v2ray-rules/releases/download/202610220935/geoip.dat",
            RoutingDataRelease.AssetUri("202610220935", "geoip.dat").AbsoluteUri);
        Assert.Equal(
            "https://github.com/Chocolate4U/Iran-v2ray-rules/releases/download/202610220935/geosite.dat.sha256sum",
            RoutingDataRelease.ChecksumUri("202610220935", "geosite.dat").AbsoluteUri);
        Assert.Throws<ArgumentException>(() => RoutingDataRelease.AssetUri("202610220935", "xray.exe"));
        Assert.Throws<ArgumentException>(() => RoutingDataRelease.AssetUri("../evil", "geoip.dat"));
    }

    [Fact]
    public void ChecksumSidecarsMatchOnlyTheNamedFile()
    {
        const string sidecar = "eed27b13f76894760a0c112474ee7904bbf5608b40054c06dfb74ba1934e5f7f  release/geoip.dat\n";

        Assert.True(RoutingDataRelease.TryParseChecksum(sidecar, "geoip.dat", out var hash));
        Assert.Equal("eed27b13f76894760a0c112474ee7904bbf5608b40054c06dfb74ba1934e5f7f", hash);
        Assert.False(RoutingDataRelease.TryParseChecksum(sidecar, "geosite.dat", out _));
        Assert.True(RoutingDataRelease.TryParseChecksum(
            "DA9BAD797C0CE095DE48BA56260C23C53CBE8871995BC4E21B6F251893CE1ED1 *geosite.dat", "geosite.dat", out hash));
        Assert.Equal("da9bad797c0ce095de48ba56260c23c53cbe8871995bc4e21b6f251893ce1ed1", hash);
        Assert.False(RoutingDataRelease.TryParseChecksum("not-a-hash  geoip.dat", "geoip.dat", out _));
        Assert.False(RoutingDataRelease.TryParseChecksum("", "geoip.dat", out _));
    }

    [Fact]
    public void MalwareRulesAreWrittenOnlyWhenRequested()
    {
        static string[] Blocked(bool blockMalware)
        {
            var json = new XrayJsonConfigurationWriter().Write(new XrayRoutingPolicy(
                RegionalRoutingPreset.IranDirect,
                XrayDomainStrategy.IPIfNonMatch,
                BypassLan: true,
                BlockAds: true,
                BlockQuic: false,
                DirectBitTorrent: false,
                BlockMalware: blockMalware));
            using var document = JsonDocument.Parse(json);
            return document.RootElement.GetProperty("routing").GetProperty("rules").EnumerateArray()
                .Where(rule => rule.GetProperty("outboundTag").GetString() == "block")
                .SelectMany(rule => rule.TryGetProperty("domain", out var domains)
                    ? domains.EnumerateArray()
                    : rule.GetProperty("ip").EnumerateArray())
                .Select(value => value.GetString()!)
                .ToArray();
        }

        Assert.Equal(["geosite:category-ads-all"], Blocked(false));
        Assert.Equal(
            ["geosite:category-ads-all", "geosite:malware", "geosite:phishing", "geosite:cryptominers",
             "geoip:malware", "geoip:phishing"],
            Blocked(true));
    }

    [Fact]
    public void RequiredCategoriesCoverEverythingTheWriterCanEmit()
    {
        Assert.Subset(
            RoutingDataRelease.RequiredSiteCategories.ToHashSet(),
            XrayJsonConfigurationWriter.MalwareSiteCategories.ToHashSet());
        Assert.Subset(
            RoutingDataRelease.RequiredIpCategories.ToHashSet(),
            XrayJsonConfigurationWriter.MalwareIpCategories.ToHashSet());
    }

    [Fact]
    public void BlockMalwareIsOnForNewProfilesAndOffForProfilesSavedBeforeIt()
    {
        Assert.True(new PaqetFireSettings().BlockMalware);

        var catalog = ProfileCatalog.Create(Guid.NewGuid(), new PaqetFireSettings { ProfileName = "Home" });
        var export = ProfileCatalogJson.WritePortableExport(catalog);
        Assert.True(ProfileCatalogJson.ReadImport(export).ActiveProfile.Settings.BlockMalware);

        var legacy = export.Replace("\"blockMalware\": true,", string.Empty, StringComparison.Ordinal);
        Assert.NotEqual(export, legacy);
        Assert.False(ProfileCatalogJson.ReadImport(legacy).ActiveProfile.Settings.BlockMalware);
    }
}
