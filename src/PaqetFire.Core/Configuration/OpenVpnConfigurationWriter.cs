using System.Text;

namespace PaqetFire.Core.Configuration;

public sealed class OpenVpnConfigurationWriter : IOpenVpnConfigurationWriter
{
    private static readonly string[] DirectivesToFilter =
    [
        "auth-user-pass",
        "socks-proxy",
        "http-proxy",
        "windows-driver",
        "dev ",
        "dev\t",
        "dev-type",
    ];

    public string GenerateRuntimeConfig(
        string originalOvpnText,
        int socksProxyPort,
        string authFilePath,
        string? socksAuthFilePath = null,
        string adapterName = "PaqetFire-VPN")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(originalOvpnText);
        ArgumentException.ThrowIfNullOrWhiteSpace(authFilePath);

        var lines = originalOvpnText.Split(["\r\n", "\r", "\n"], StringSplitOptions.None);
        var builder = new StringBuilder();

        foreach (var line in lines)
        {
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith('#') || trimmed.StartsWith(';'))
            {
                builder.AppendLine(line);
                continue;
            }

            var shouldFilter = false;
            foreach (var directive in DirectivesToFilter)
            {
                if (trimmed.StartsWith(directive, StringComparison.OrdinalIgnoreCase))
                {
                    shouldFilter = true;
                    break;
                }
            }

            if (!shouldFilter)
            {
                builder.AppendLine(line);
            }
        }

        builder.AppendLine();
        builder.AppendLine("# === PaqetFire Upstream OpenVPN Runtime Directives ===");
        builder.AppendLine("dev-type tun");
        builder.AppendLine("dev tun");
        var normalizedSocksAuth = socksAuthFilePath?.Replace('\\', '/');
        var normalizedProtonAuth = authFilePath.Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(normalizedSocksAuth))
        {
            builder.AppendLine($"socks-proxy 127.0.0.1 {socksProxyPort}");
        }
        else
        {
            builder.AppendLine($"socks-proxy 127.0.0.1 {socksProxyPort} \"{normalizedSocksAuth}\"");
        }
        builder.AppendLine($"auth-user-pass \"{normalizedProtonAuth}\"");
        builder.AppendLine("route-metric 1");

        return builder.ToString();
    }
}
