using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace PaqetFire.Broker.Engines;

/// <summary>
/// Detects external VPN clients (such as standalone OpenVPN GUI or service) that may hold
/// conflicting WFP filters or route gateways that prevent Paqet from sending packets.
/// </summary>
public interface IVpnConflictDetector
{
    bool TryFindConflictingVpn(
        string? allowedOpenVpnExecutablePath,
        [NotNullWhen(true)] out string? conflictDescription);
}

internal sealed class ProcessVpnConflictDetector : IVpnConflictDetector
{
    public static ProcessVpnConflictDetector Instance { get; } = new();

    public bool TryFindConflictingVpn(
        string? allowedOpenVpnExecutablePath,
        [NotNullWhen(true)] out string? conflictDescription)
    {
        conflictDescription = null;
        var normalizedAllowed = string.IsNullOrWhiteSpace(allowedOpenVpnExecutablePath)
            ? null
            : OrphanedEngineCleanup.Normalize(allowedOpenVpnExecutablePath);

        var conflictingProcesses = new List<string>();

        foreach (var process in Process.GetProcessesByName("openvpn"))
        {
            using (process)
            {
                try
                {
                    var path = process.MainModule?.FileName;
                    if (string.IsNullOrEmpty(path))
                    {
                        conflictingProcesses.Add($"OpenVPN (PID {process.Id})");
                        continue;
                    }

                    var normalized = OrphanedEngineCleanup.Normalize(path);
                    if (normalizedAllowed is null || !string.Equals(normalized, normalizedAllowed, StringComparison.OrdinalIgnoreCase))
                    {
                        conflictingProcesses.Add($"OpenVPN at {path} (PID {process.Id})");
                    }
                }
                catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
                {
                    // If we cannot query MainModule of another user's or service's openvpn.exe, it is external
                    conflictingProcesses.Add($"OpenVPN (PID {process.Id})");
                }
            }
        }

        foreach (var process in Process.GetProcessesByName("openvpn-gui"))
        {
            using (process)
            {
                conflictingProcesses.Add($"OpenVPN GUI (PID {process.Id})");
            }
        }

        if (conflictingProcesses.Count > 0)
        {
            conflictDescription = string.Join(", ", conflictingProcesses);
            return true;
        }

        return false;
    }
}
