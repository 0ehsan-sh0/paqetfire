using System.Globalization;
using System.Text.RegularExpressions;

namespace PaqetFire.Core.RoutingData;

/// <summary>Where the GeoIP/GeoSite files Xray loads come from.</summary>
public enum RoutingDataOrigin
{
    // The files pinned in the payload manifest, shipped with Xray.
    Bundled,
    // A verified Iran-v2ray-rules release downloaded by the broker.
    Updated,
}

public enum RoutingDataAction
{
    UpdateNow,
    EnableAutomaticUpdates,
    DisableAutomaticUpdates,
    UseBundled,
}

/// <param name="Version">The release tag, or the bundled Xray version.</param>
/// <param name="SupportsMalwareCategories">Whether the active files carry the malware, phishing, and cryptominer categories.</param>
public sealed record RoutingDataStatus(
    RoutingDataOrigin Origin,
    string Version,
    DateTimeOffset? InstalledAt,
    DateTimeOffset? LastCheckedAt,
    bool AutomaticUpdates,
    bool IsUpdating,
    bool SupportsMalwareCategories,
    string? LastError = null);

/// <summary>The upstream release contract for downloaded routing data.</summary>
public static partial class RoutingDataRelease
{
    public const string Repository = "Chocolate4U/Iran-v2ray-rules";

    public const string GeoIpFileName = "geoip.dat";

    public const string GeoSiteFileName = "geosite.dat";

    public static IReadOnlyList<string> FileNames { get; } = [GeoIpFileName, GeoSiteFileName];

    // Current releases are about 17 MB and 8 MB; the cap rejects a runaway download.
    public const long MaximumFileBytes = 32L * 1024 * 1024;

    /// <summary>How far ahead of the local clock a release tag may be, to allow for clock skew.</summary>
    public static TimeSpan MaximumTagSkew { get; } = TimeSpan.FromDays(1);

    public static TimeSpan AutomaticCheckInterval { get; } = TimeSpan.FromDays(1);

    public static Uri LatestReleaseApiUri { get; } =
        new($"https://api.github.com/repos/{Repository}/releases/latest");

    /// <summary>Every category PaqetFire can reference; a release missing one is rejected.</summary>
    public static IReadOnlyList<string> RequiredSiteCategories { get; } =
        ["geosite:private", "geosite:category-ir", "geosite:category-ads-all",
         "geosite:malware", "geosite:phishing", "geosite:cryptominers"];

    public static IReadOnlyList<string> RequiredIpCategories { get; } =
        ["geoip:private", "geoip:ir", "geoip:malware", "geoip:phishing"];

    // Releases are tagged with their UTC build time, for example 202610220935.
    public static bool IsValidTag(string? tag) =>
        tag is not null &&
        TagPattern().IsMatch(tag) &&
        DateTime.TryParseExact(tag, "yyyyMMddHHmm", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out _);

    /// <summary>
    /// A valid tag that is not more than <see cref="MaximumTagSkew"/> ahead of
    /// <paramref name="now"/>. A far-future tag would otherwise outrank every real release.
    /// </summary>
    public static bool IsValidTag(string? tag, DateTimeOffset now) =>
        IsValidTag(tag) &&
        DateTime.TryParseExact(tag, "yyyyMMddHHmm", CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var released) &&
        released <= now.UtcDateTime + MaximumTagSkew;

    /// <summary>Newer tags sort later because they are fixed-width timestamps.</summary>
    public static bool IsNewer(string candidate, string? installed) =>
        IsValidTag(candidate) &&
        (!IsValidTag(installed) || string.CompareOrdinal(candidate, installed) > 0);

    public static Uri AssetUri(string tag, string fileName)
    {
        if (!IsValidTag(tag) || !FileNames.Contains(fileName, StringComparer.Ordinal))
        {
            throw new ArgumentException("The routing data release asset is invalid.");
        }

        return new Uri($"https://github.com/{Repository}/releases/download/{tag}/{fileName}");
    }

    public static Uri ChecksumUri(string tag, string fileName) =>
        new(AssetUri(tag, fileName) + ".sha256sum");

    /// <summary>
    /// Reads a <c>sha256sum</c> line such as <c>&lt;hex&gt;  release/geoip.dat</c> and
    /// returns the digest only when the named file matches.
    /// </summary>
    public static bool TryParseChecksum(string? content, string fileName, out string sha256)
    {
        sha256 = string.Empty;
        if (string.IsNullOrWhiteSpace(content))
        {
            return false;
        }

        foreach (var line in content.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var match = ChecksumLinePattern().Match(line);
            if (!match.Success)
            {
                continue;
            }

            var name = match.Groups["name"].Value.TrimStart('*');
            var leaf = name[(name.LastIndexOf('/') + 1)..];
            if (string.Equals(leaf, fileName, StringComparison.Ordinal))
            {
                sha256 = match.Groups["hash"].Value.ToLowerInvariant();
                return true;
            }
        }

        return false;
    }

    [GeneratedRegex("^[0-9]{12}$", RegexOptions.CultureInvariant)]
    private static partial Regex TagPattern();

    [GeneratedRegex(@"^(?<hash>[0-9a-fA-F]{64})\s+(?<name>\S+)$", RegexOptions.CultureInvariant)]
    private static partial Regex ChecksumLinePattern();
}
