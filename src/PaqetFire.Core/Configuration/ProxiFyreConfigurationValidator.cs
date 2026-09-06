using PaqetFire.Core.Routing;

namespace PaqetFire.Core.Configuration;

public static class ProxiFyreConfigurationValidator
{
    public static IReadOnlyList<string> Validate(
        ProxiFyreRoutePlan? routePlan,
        IReadOnlyCollection<string>? lockedExclusions)
    {
        if (routePlan is null)
        {
            return ["A ProxiFyre route plan is required."];
        }

        var errors = new List<string>();

        if (!Enum.IsDefined(routePlan.LogLevel))
        {
            errors.Add("The ProxiFyre log level is invalid.");
        }

        if (routePlan.Rules is null || routePlan.Rules.Count == 0)
        {
            errors.Add("At least one ProxiFyre routing rule is required.");
        }
        else
        {
            ValidateRules(routePlan.Rules, errors);
        }

        ValidateLockedExclusions(routePlan.Exclusions, lockedExclusions, errors);
        return errors;
    }

    private static void ValidateRules(IReadOnlyList<ProxyRule> rules, ICollection<string> errors)
    {
        var catchAllIndex = -1;

        for (var index = 0; index < rules.Count; index++)
        {
            var rule = rules[index];
            var label = $"Routing rule {index + 1}";
            if (rule is null)
            {
                errors.Add($"{label} is missing.");
                continue;
            }

            if (string.IsNullOrWhiteSpace(rule.Name) ||
                ConfigurationValuePolicy.ContainsControlCharacter(rule.Name))
            {
                errors.Add($"{label} must have a name with no control characters.");
            }

            if (rule.Applications is null || rule.Applications.Count == 0)
            {
                errors.Add($"{label} must select at least one application.");
            }
            else
            {
                var containsCatchAll = rule.Applications.Any(application => application == string.Empty);
                if (containsCatchAll)
                {
                    if (rule.Applications.Count != 1)
                    {
                        errors.Add($"{label} must not combine the catch-all application with other applications.");
                    }

                    if (catchAllIndex >= 0)
                    {
                        errors.Add("Only one catch-all ProxiFyre routing rule is allowed.");
                    }

                    catchAllIndex = index;
                }

                if (rule.Applications.Any(application =>
                        application is null ||
                        (application.Length > 0 && string.IsNullOrWhiteSpace(application)) ||
                        application.Length > 1024 ||
                        ConfigurationValuePolicy.ContainsControlCharacter(application)))
                {
                    errors.Add($"{label} contains an invalid application name or path.");
                }
            }

            if (!ConfigurationValuePolicy.TryNormalizeEndpoint(rule.Socks5Endpoint, out _))
            {
                errors.Add($"{label} has an invalid SOCKS5 endpoint.");
            }
            else if (rule.Socks5Endpoint.Trim().StartsWith("[", StringComparison.Ordinal))
            {
                errors.Add(
                    $"{label} uses an IPv6-literal SOCKS5 endpoint, but ProxiFyre requires an IPv4 upstream.");
            }

            if (!rule.RouteTcp && !rule.RouteUdp)
            {
                errors.Add($"{label} must enable TCP, UDP, or both.");
            }

            if (!rule.RouteIpv4 && !rule.RouteIpv6)
            {
                errors.Add($"{label} must enable IPv4, IPv6, or both.");
            }

            ValidateCredentials(rule, label, errors);
            ValidateTransport(rule, label, errors);
        }

        if (catchAllIndex >= 0 && catchAllIndex != rules.Count - 1)
        {
            errors.Add("The catch-all ProxiFyre routing rule must be last because rules use first-match ordering.");
        }
    }

    private static void ValidateCredentials(
        ProxyRule rule,
        string label,
        ICollection<string> errors)
    {
        var hasUsername = rule.Username is not null;
        var hasPassword = rule.Password is not null;
        if (hasUsername != hasPassword)
        {
            errors.Add($"{label} must provide both SOCKS5 username and password, or neither.");
            return;
        }

        if (!hasUsername)
        {
            return;
        }

        if (!ConfigurationValuePolicy.FitsSocks5Credential(rule.Username))
        {
            errors.Add($"{label} has a SOCKS5 username longer than 255 UTF-8 bytes.");
        }

        if (!ConfigurationValuePolicy.FitsSocks5Credential(rule.Password))
        {
            errors.Add($"{label} has a SOCKS5 password longer than 255 UTF-8 bytes.");
        }
    }

    private static void ValidateTransport(
        ProxyRule rule,
        string label,
        ICollection<string> errors)
    {
        if (!Enum.IsDefined(rule.Transport))
        {
            errors.Add($"{label} has an invalid SOCKS5 transport.");
            return;
        }

        var hasServerName = !string.IsNullOrWhiteSpace(rule.TlsServerName);
        var hasPin = !string.IsNullOrWhiteSpace(rule.TlsPinnedSha256);
        var hasTlsOnlySetting = hasServerName || hasPin || rule.TlsAllowInvalidCertificate;
        if (rule.Transport != Socks5Transport.Tls)
        {
            if (hasTlsOnlySetting)
            {
                errors.Add($"{label} must use TLS before TLS certificate options can be configured.");
            }

            return;
        }

        if (rule.TlsServerName is not null &&
            (string.IsNullOrWhiteSpace(rule.TlsServerName) ||
             rule.TlsServerName.Length > 253 ||
             ConfigurationValuePolicy.ContainsControlCharacter(rule.TlsServerName)))
        {
            errors.Add($"{label} has an invalid TLS server name.");
        }

        if (rule.TlsPinnedSha256 is not null &&
            !ConfigurationValuePolicy.TryNormalizeSha256Fingerprint(
                rule.TlsPinnedSha256,
                out _))
        {
            errors.Add($"{label} must use a 64-hex-character TLS certificate SHA-256 pin.");
        }
    }

    private static void ValidateLockedExclusions(
        IReadOnlyList<string>? exclusions,
        IReadOnlyCollection<string>? lockedExclusions,
        ICollection<string> errors)
    {
        if (lockedExclusions is null || lockedExclusions.Count == 0)
        {
            errors.Add("At least one locked engine exclusion is required.");
            return;
        }

        if (exclusions is null)
        {
            errors.Add("The ProxiFyre exclusion list is missing.");
            return;
        }

        if (exclusions.Any(IsInvalidEntry))
        {
            errors.Add("The ProxiFyre exclusion list contains an invalid application name or path.");
        }

        if (lockedExclusions.Any(IsInvalidEntry))
        {
            errors.Add("The locked engine exclusion list contains an invalid application name or path.");
            return;
        }

        var configured = new HashSet<string>(
            exclusions.Select(exclusion => exclusion.Trim()),
            StringComparer.OrdinalIgnoreCase);

        foreach (var lockedExclusion in lockedExclusions
                     .Select(exclusion => exclusion.Trim())
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!configured.Contains(lockedExclusion))
            {
                errors.Add($"Locked engine exclusion '{lockedExclusion}' is missing from the route plan.");
            }
        }
    }

    private static bool IsInvalidEntry(string? value) =>
        string.IsNullOrWhiteSpace(value) ||
        value.Length > 1024 ||
        ConfigurationValuePolicy.ContainsControlCharacter(value);
}
