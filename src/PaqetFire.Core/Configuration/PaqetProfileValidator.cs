namespace PaqetFire.Core.Configuration;

/// <summary>
/// Compatibility entry point for callers that validate a profile before a key
/// has been collected. Full validation is performed when YAML is generated.
/// </summary>
public static class PaqetProfileValidator
{
    public static IReadOnlyList<string> Validate(PaqetProfile profile) =>
        PaqetConfigurationValidator.Validate(profile, "validation-placeholder");
}
