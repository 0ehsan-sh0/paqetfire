using System.Globalization;
using System.Net;
using System.Text;

namespace PaqetFire.Core.Configuration;

internal static class ConfigurationValuePolicy
{
    public static bool ContainsControlCharacter(string value) => value.Any(char.IsControl);

    public static bool FitsSocks5Credential(string? value) =>
        value is not null && Encoding.UTF8.GetByteCount(value) <= byte.MaxValue;

    public static bool TryNormalizeSha256Fingerprint(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value) || ContainsControlCharacter(value))
        {
            return false;
        }

        var builder = new StringBuilder(64);
        foreach (var character in value)
        {
            if (char.IsWhiteSpace(character) || character is ':' or '-')
            {
                continue;
            }

            if (!Uri.IsHexDigit(character))
            {
                return false;
            }

            builder.Append(char.ToLowerInvariant(character));
        }

        if (builder.Length != 64)
        {
            return false;
        }

        normalized = builder.ToString();
        return true;
    }

    public static bool TryNormalizeEndpoint(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value) || ContainsControlCharacter(value))
        {
            return false;
        }

        var candidate = value.Trim();
        string host;
        string portText;

        if (candidate.StartsWith("[", StringComparison.Ordinal))
        {
            var bracket = candidate.IndexOf(']');
            if (bracket <= 1 || bracket + 2 >= candidate.Length || candidate[bracket + 1] != ':')
            {
                return false;
            }

            host = candidate[1..bracket];
            portText = candidate[(bracket + 2)..];
            if (!IPAddress.TryParse(host, out var ipv6) ||
                ipv6.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6)
            {
                return false;
            }

            host = ipv6.ToString();
        }
        else
        {
            var separator = candidate.LastIndexOf(':');
            if (separator <= 0 || separator == candidate.Length - 1 ||
                candidate[..separator].Contains(':', StringComparison.Ordinal))
            {
                return false;
            }

            host = candidate[..separator];
            portText = candidate[(separator + 1)..];

            if (IPAddress.TryParse(host, out var address))
            {
                if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
                {
                    return false;
                }

                host = address.ToString();
            }
            else if (Uri.CheckHostName(host) != UriHostNameType.Dns)
            {
                return false;
            }
            else
            {
                try
                {
                    host = new IdnMapping().GetAscii(host).ToLowerInvariant();
                }
                catch (ArgumentException)
                {
                    return false;
                }
            }
        }

        if (!ushort.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out var port) ||
            port == 0)
        {
            return false;
        }

        normalized = host.Contains(':', StringComparison.Ordinal)
            ? $"[{host}]:{port}"
            : $"{host}:{port}";
        return true;
    }
}
