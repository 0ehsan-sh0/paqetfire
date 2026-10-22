using System.Globalization;
using PaqetFire.Core.RoutingData;

namespace PaqetFire.Desktop.Presentation;

/// <summary>Text for the Settings page's routing data card.</summary>
public static class RoutingDataText
{
    /// <summary>"Iran-v2ray-rules 2026-10-22 09:35 UTC" or "Bundled with Xray 26.3.27".</summary>
    public static string Source(RoutingDataStatus? status)
    {
        if (status is null)
        {
            return "Waiting for the broker…";
        }

        if (status.Origin == RoutingDataOrigin.Updated &&
            DateTime.TryParseExact(status.Version, "yyyyMMddHHmm", CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var released))
        {
            return string.Create(CultureInfo.InvariantCulture, $"Iran-v2ray-rules {released:yyyy-MM-dd HH:mm} UTC");
        }

        return status.Origin == RoutingDataOrigin.Updated
            ? $"Iran-v2ray-rules {status.Version}"
            : $"Bundled with {status.Version}";
    }

    public static string Checked(RoutingDataStatus? status)
    {
        if (status is null)
        {
            return string.Empty;
        }

        if (status.IsUpdating)
        {
            return "Downloading and verifying the latest release…";
        }

        var checkedText = status.LastCheckedAt is { } lastChecked
            ? $"Last checked {lastChecked.ToLocalTime():g}"
            : "Not checked yet";
        return status.InstalledAt is { } installed
            ? $"{checkedText} · installed {installed.ToLocalTime():g}"
            : checkedText;
    }

    /// <summary>The InfoBar shown when an update the user asked for has finished.</summary>
    public static (bool Succeeded, string Title, string Message) UpdateResult(string? previousVersion, RoutingDataStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        if (status.LastError is { } error)
        {
            return (false, "Routing data was not updated", error);
        }

        return string.Equals(previousVersion, status.Version, StringComparison.Ordinal)
            ? (true, "Routing data is up to date", $"{Source(status)} is the latest release.")
            : (true, "Routing data updated", $"{Source(status)} is used from the next connection.");
    }
}
