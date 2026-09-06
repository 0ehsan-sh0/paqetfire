using PaqetFire.Core.Deployment;

namespace PaqetFire.Broker.Deployment;

public sealed class PrerequisiteInspector
{
    private static readonly Uri NpcapHelp = new("https://npcap.com/#download");
    private static readonly Uri WinpkFilterHelp = new("https://github.com/wiresock/ndisapi/releases");

    public IReadOnlyList<PrerequisiteStatus> Inspect() =>
    [
        InspectNpcap(),
        InspectWinpkFilter(),
    ];

    private static PrerequisiteStatus InspectNpcap()
    {
        var paths = new[]
        {
            Path.Combine(Environment.SystemDirectory, "Npcap", "wpcap.dll"),
            Path.Combine(Environment.SystemDirectory, "wpcap.dll"),
        };
        var installed = paths.FirstOrDefault(File.Exists);
        return new PrerequisiteStatus(
            "npcap",
            "Npcap packet capture driver",
            installed is not null,
            installed is null
                ? "Required by Paqet. Install Npcap with WinPcap-compatible mode enabled."
                : $"Detected at {installed}.",
            NpcapHelp);
    }

    private static PrerequisiteStatus InspectWinpkFilter()
    {
        var path = Path.Combine(Environment.SystemDirectory, "drivers", "ndisrd.sys");
        var installed = File.Exists(path);
        return new PrerequisiteStatus(
            "winpkfilter",
            "Windows Packet Filter driver",
            installed,
            installed
                ? $"Detected at {path}."
                : "Required by ProxiFyre. Install the architecture-matched signed driver.",
            WinpkFilterHelp);
    }
}
