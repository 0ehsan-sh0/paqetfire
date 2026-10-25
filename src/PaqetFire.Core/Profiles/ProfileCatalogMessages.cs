namespace PaqetFire.Core.Profiles;

public enum ProfileActionKind
{
    Rename,
    Duplicate,
    Delete,
    Activate,
    MakeDefault,
    ImportRedacted,
    Import,
    FactoryReset,
    // Adds a new profile with default settings, named by Name.
    Create,
    // Adds the profiles in Payload beside the existing ones instead of replacing the catalog.
    ImportMerge,
}

public sealed record ProfileAction(
    ProfileActionKind Kind,
    Guid? ProfileId = null,
    string? Name = null,
    string? Payload = null);

public sealed record ConnectionProfileSummary(
    Guid Id,
    string Name,
    bool IsActive,
    bool IsDefault,
    bool HasTransportKey,
    bool HasSharingPassword,
    bool HasUpstreamOpenVpnPassword = false);

public sealed record ProfileCatalogView(IReadOnlyList<ConnectionProfileSummary> Profiles)
{
    public static ProfileCatalogView FromCatalog(ProfileCatalog catalog) => new(
        catalog.Profiles.Select(profile => new ConnectionProfileSummary(
            profile.Id,
            profile.Settings.ProfileName,
            profile.Id == catalog.ActiveProfileId,
            profile.Id == catalog.DefaultProfileId,
            !string.IsNullOrEmpty(profile.Settings.TransportKey),
            !string.IsNullOrEmpty(profile.Settings.LanSocksPassword),
            !string.IsNullOrEmpty(profile.Settings.UpstreamOpenVpn.Password))).ToArray());
}
