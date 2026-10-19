using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace PaqetFire.Core.Diagnostics;

/// <summary>
/// Makes diagnostic text safe to paste into a public issue: secrets and the server
/// host become placeholders, and public addresses (destinations, exit and resolver
/// addresses) are hidden. Loopback, private, and link-local addresses stay readable.
/// </summary>
public static partial class DiagnosticRedactor
{
    public const string SecretPlaceholder = "[secret]";
    public const string ServerPlaceholder = "[server]";
    public const string PublicAddressPlaceholder = "[public-ip]";

    public static string Redact(string text, IEnumerable<string?> secrets, string? serverHost = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        foreach (var secret in secrets.Where(secret => !string.IsNullOrEmpty(secret)).Distinct().OrderByDescending(secret => secret!.Length))
        {
            text = text.Replace(secret!, SecretPlaceholder, StringComparison.Ordinal);
        }

        if (!string.IsNullOrWhiteSpace(serverHost))
        {
            text = text.Replace(serverHost.Trim('[', ']'), ServerPlaceholder, StringComparison.OrdinalIgnoreCase);
        }

        text = Ipv4Pattern().Replace(text, match => RedactAddress(match.Value));
        return Ipv6Pattern().Replace(text, match => RedactAddress(match.Value));
    }

    public static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
        {
            return false;
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return !(address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6UniqueLocal || address.IsIPv6Multicast);
        }

        var bytes = address.GetAddressBytes();
        return bytes[0] switch
        {
            10 => false,
            172 => bytes[1] is < 16 or > 31,
            192 => bytes[1] != 168,
            169 => bytes[1] != 254,
            100 => bytes[1] is < 64 or > 127,
            198 => bytes[1] is not (18 or 19),
            >= 224 => false,
            _ => true,
        };
    }

    private static string RedactAddress(string candidate) =>
        IPAddress.TryParse(candidate, out var address) && IsPublic(address) ? PublicAddressPlaceholder : candidate;

    [GeneratedRegex(@"(?<![\d.])(?:\d{1,3}\.){3}\d{1,3}(?![\d.])")]
    private static partial Regex Ipv4Pattern();

    [GeneratedRegex(@"(?<![0-9A-Fa-f:])(?:[0-9A-Fa-f]{0,4}:){2,7}[0-9A-Fa-f]{0,4}(?![0-9A-Fa-f:])")]
    private static partial Regex Ipv6Pattern();
}
