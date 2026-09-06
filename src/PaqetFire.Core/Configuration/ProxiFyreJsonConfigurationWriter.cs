using System.Text;
using System.Text.Json;
using PaqetFire.Core.Routing;

namespace PaqetFire.Core.Configuration;

public sealed class ProxiFyreJsonConfigurationWriter : IProxiFyreConfigurationWriter
{
    public string Write(
        ProxiFyreRoutePlan routePlan,
        IReadOnlyCollection<string> lockedExclusions)
    {
        var errors = ProxiFyreConfigurationValidator.Validate(routePlan, lockedExclusions);
        if (errors.Count > 0)
        {
            throw new ConfigurationValidationException(errors);
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteString("logLevel", routePlan.LogLevel.ToString());
            writer.WriteBoolean("bypassLan", routePlan.BypassLan);
            writer.WriteStartArray("proxies");

            foreach (var rule in routePlan.Rules)
            {
                WriteRule(writer, rule);
            }

            writer.WriteEndArray();
            writer.WriteStartArray("excludes");
            foreach (var exclusion in routePlan.Exclusions
                         .Select(exclusion => exclusion.Trim())
                         .Distinct(StringComparer.OrdinalIgnoreCase)
                         .Order(StringComparer.OrdinalIgnoreCase)
                         .ThenBy(exclusion => exclusion, StringComparer.Ordinal))
            {
                writer.WriteStringValue(exclusion);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray()) + "\n";
    }

    private static void WriteRule(Utf8JsonWriter writer, ProxyRule rule)
    {
        writer.WriteStartObject();
        writer.WriteStartArray("appNames");
        foreach (var application in rule.Applications
                     .Select(application => application.Trim())
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .Order(StringComparer.OrdinalIgnoreCase)
                     .ThenBy(application => application, StringComparer.Ordinal))
        {
            writer.WriteStringValue(application);
        }

        writer.WriteEndArray();
        _ = ConfigurationValuePolicy.TryNormalizeEndpoint(rule.Socks5Endpoint, out var endpoint);
        writer.WriteString("socks5ProxyEndpoint", endpoint);
        if (rule.Username is not null && rule.Password is not null)
        {
            writer.WriteString("username", rule.Username);
            writer.WriteString("password", rule.Password);
        }

        writer.WriteString(
            "socks5Transport",
            rule.Transport == Socks5Transport.Tls ? "TLS" : "TCP");
        if (rule.Transport == Socks5Transport.Tls)
        {
            if (!string.IsNullOrWhiteSpace(rule.TlsServerName))
            {
                writer.WriteString("tlsServerName", rule.TlsServerName.Trim());
            }

            if (!string.IsNullOrWhiteSpace(rule.TlsPinnedSha256))
            {
                _ = ConfigurationValuePolicy.TryNormalizeSha256Fingerprint(
                    rule.TlsPinnedSha256,
                    out var fingerprint);
                writer.WriteString("tlsPinnedSha256", fingerprint);
            }

            writer.WriteBoolean(
                "tlsAllowInvalidCertificate",
                rule.TlsAllowInvalidCertificate);
        }

        writer.WriteStartArray("supportedProtocols");
        if (rule.RouteTcp)
        {
            writer.WriteStringValue("TCP");
        }

        if (rule.RouteUdp)
        {
            writer.WriteStringValue("UDP");
        }

        writer.WriteEndArray();
        writer.WriteStartArray("supportedAddressFamilies");
        if (rule.RouteIpv4)
        {
            writer.WriteStringValue("IPv4");
        }

        if (rule.RouteIpv6)
        {
            writer.WriteStringValue("IPv6");
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }
}
