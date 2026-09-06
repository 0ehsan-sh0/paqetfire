using Microsoft.Win32;
using System.Diagnostics;

namespace PaqetFire.Broker.Configuration;

public sealed record PaqetPrerequisiteStatus(
    bool NpcapInstalled,
    string? NpcapVersion,
    string Detail);

public sealed class PaqetPrerequisiteInspector
{
    public PaqetPrerequisiteStatus Inspect()
    {
        var systemDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var npcapDirectory = Path.Combine(systemDirectory, "Npcap");
        var packetLibrary = Path.Combine(npcapDirectory, "Packet.dll");
        var pcapLibrary = Path.Combine(npcapDirectory, "wpcap.dll");
        using var serviceKey = Registry.LocalMachine.OpenSubKey(
            @"SYSTEM\CurrentControlSet\Services\npcap",
            writable: false);
        var serviceRegistered = serviceKey is not null;
        var installed = serviceRegistered && File.Exists(packetLibrary) && File.Exists(pcapLibrary);

        string? version = null;
        using (var uninstall = Registry.LocalMachine.OpenSubKey(
                   @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\NpcapInst",
                   writable: false))
        {
            version = uninstall?.GetValue("DisplayVersion") as string;
        }

        if (installed && string.IsNullOrWhiteSpace(version))
        {
            version = FileVersionInfo.GetVersionInfo(pcapLibrary).FileVersion;
        }

        return new PaqetPrerequisiteStatus(
            installed,
            version,
            installed
                ? "Npcap and its packet-capture service are installed."
                : "Npcap is required. Install the bundled prerequisite before starting Paqet.");
    }
}
