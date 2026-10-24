using System.Text.RegularExpressions;

namespace PaqetFire.Core.Configuration;

public static class UpstreamOpenVpnValidator
{
    private static readonly Regex RemoteRegex = new(
        @"^\s*remote\s+(?<host>[^\s]+)(?:\s+(?<port>\d+))?",
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly Regex ProtoRegex = new(
        @"^\s*proto\s+(?<proto>[^\s]+)",
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly Regex AuthUserPassRegex = new(
        @"^\s*auth-user-pass",
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled);

    public static IReadOnlyList<string> Validate(UpstreamOpenVpnOptions? options)
    {
        if (options is null || !options.Enabled)
        {
            return [];
        }

        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(options.OvpnConfigText))
        {
            errors.Add("OpenVPN configuration content is required when Upstream OpenVPN is enabled.");
            return errors;
        }

        if (options.OvpnConfigText.Length > 2 * 1024 * 1024)
        {
            errors.Add("OpenVPN configuration content must not exceed 2 MB.");
        }

        var (remoteHost, remotePort, _) = ParseServerInfo(options.OvpnConfigText);
        if (string.IsNullOrEmpty(remoteHost) || remotePort is null or <= 0 or > 65535)
        {
            errors.Add("The OpenVPN configuration must contain at least one valid 'remote <host> <port>' directive.");
        }

        var requiresAuth = AuthUserPassRegex.IsMatch(options.OvpnConfigText);
        if (requiresAuth)
        {
            if (string.IsNullOrWhiteSpace(options.Username))
            {
                errors.Add("An OpenVPN username is required by this configuration.");
            }

            if (string.IsNullOrWhiteSpace(options.Password))
            {
                errors.Add("An OpenVPN password is required by this configuration.");
            }
        }

        return errors;
    }

    public static (string? RemoteHost, int? RemotePort, string? Protocol) ParseServerInfo(string ovpnContent)
    {
        if (string.IsNullOrWhiteSpace(ovpnContent))
        {
            return (null, null, null);
        }

        string? host = null;
        int? port = null;
        string? proto = null;

        var remoteMatch = RemoteRegex.Match(ovpnContent);
        if (remoteMatch.Success)
        {
            host = remoteMatch.Groups["host"].Value;
            if (remoteMatch.Groups["port"].Success &&
                int.TryParse(remoteMatch.Groups["port"].Value, out var parsedPort))
            {
                port = parsedPort;
            }
            else
            {
                port = 1194;
            }
        }

        var protoMatch = ProtoRegex.Match(ovpnContent);
        if (protoMatch.Success)
        {
            var rawProto = protoMatch.Groups["proto"].Value.ToLowerInvariant();
            if (rawProto.StartsWith("tcp", StringComparison.Ordinal))
            {
                proto = "tcp";
            }
            else if (rawProto.StartsWith("udp", StringComparison.Ordinal))
            {
                proto = "udp";
            }
            else
            {
                proto = rawProto;
            }
        }

        return (host, port, proto);
    }
}
