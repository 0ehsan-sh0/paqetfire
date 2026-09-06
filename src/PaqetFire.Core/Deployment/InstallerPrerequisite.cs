namespace PaqetFire.Core.Deployment;

public enum PrerequisiteAcquisition
{
    Embedded,
    OfficialDownload,
}

public sealed record InstallerPrerequisite(
    string Id,
    string Version,
    PrerequisiteAcquisition Acquisition,
    string LicenseNotice,
    Uri? OfficialUri = null,
    string? Sha256 = null);
