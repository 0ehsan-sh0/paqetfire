namespace PaqetFire.Core.Deployment;

public sealed record PrerequisiteStatus(
    string Id,
    string DisplayName,
    bool IsInstalled,
    string Detail,
    Uri? HelpUri = null);
