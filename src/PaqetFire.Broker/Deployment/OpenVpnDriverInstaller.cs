using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace PaqetFire.Broker.Deployment;

public static class OpenVpnDriverInstaller
{
    public static async ValueTask<bool> EnsureDriverInstalledAsync(string payloadRoot, ILogger? logger = null)
    {
        var driverSys = Path.Combine(Environment.SystemDirectory, "drivers", "ovpn-dco.sys");
        if (File.Exists(driverSys))
        {
            return true;
        }

        var infPath = Path.Combine(payloadRoot, "engines", "openvpn", "x64", "driver", "ovpn-dco.inf");
        if (!File.Exists(infPath))
        {
            logger?.LogWarning("Bundled ovpn-dco.inf not found at {Path}", infPath);
            return false;
        }

        try
        {
            logger?.LogInformation("Installing bundled OpenVPN DCO driver from {InfPath}...", infPath);
            var startInfo = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "pnputil.exe"),
                Arguments = $"/add-driver \"{infPath}\" /install",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var process = Process.Start(startInfo);
            if (process is not null)
            {
                await process.WaitForExitAsync().ConfigureAwait(false);
                logger?.LogInformation("pnputil exited with code {ExitCode}", process.ExitCode);
                return process.ExitCode == 0 || process.ExitCode == 259 || process.ExitCode == 3010;
            }
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Failed to provision OpenVPN DCO driver.");
        }

        return File.Exists(driverSys);
    }
}
