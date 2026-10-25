namespace PaqetFire.Broker.Engines;

public static class OpenVpnLocator
{
    private static readonly string[] StandardLocations =
    [
        @"C:\Program Files\OpenVPN\bin\openvpn.exe",
        @"C:\Program Files (x86)\OpenVPN\bin\openvpn.exe",
    ];

    public static string? FindExecutable(string payloadRoot)
    {
        if (!string.IsNullOrWhiteSpace(payloadRoot))
        {
            var bundled = Path.Combine(payloadRoot, "engines", "openvpn", "x64", "openvpn.exe");
            if (File.Exists(bundled))
            {
                return bundled;
            }
        }

        foreach (var location in StandardLocations)
        {
            if (File.Exists(location))
            {
                return location;
            }
        }

        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathEnv))
        {
            return null;
        }

        foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var candidate = Path.Combine(dir, "openvpn.exe");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
