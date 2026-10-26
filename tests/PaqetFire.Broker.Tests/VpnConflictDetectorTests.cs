using System.Diagnostics;
using PaqetFire.Broker.Engines;
using Xunit;

namespace PaqetFire.Broker.Tests;

public sealed class VpnConflictDetectorTests
{
    [Fact]
    public void TryFindConflictingVpn_WhenNoExternalOpenVpnRuns_ReturnsFalse()
    {
        var allowedPath = Path.Combine(Environment.SystemDirectory, "notepad.exe");
        var hasConflict = ProcessVpnConflictDetector.Instance.TryFindConflictingVpn(allowedPath, out var conflict);

        var openVpnProcesses = Process.GetProcessesByName("openvpn");
        if (openVpnProcesses.Length == 0 && Process.GetProcessesByName("openvpn-gui").Length == 0)
        {
            Assert.False(hasConflict);
            Assert.Null(conflict);
        }
    }

    [Fact]
    public void TryFindConflictingVpn_DetectsProcessNotMatchingAllowedPath()
    {
        var directory = Path.Combine(Path.GetTempPath(), "PaqetFire-test-vpn-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        var dummyExe = Path.Combine(directory, "openvpn.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "ping.exe"), dummyExe);

        using var process = Process.Start(new ProcessStartInfo(dummyExe, "-t 127.0.0.1") { CreateNoWindow = true })!;
        try
        {
            var allowedPath = @"C:\Program Files\PaqetFire\broker\payload\engines\openvpn\x64\openvpn.exe";
            var hasConflict = ProcessVpnConflictDetector.Instance.TryFindConflictingVpn(allowedPath, out var conflict);

            Assert.True(hasConflict);
            Assert.NotNull(conflict);
            Assert.Contains("openvpn", conflict, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill();
                process.WaitForExit();
            }
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch
            {
            }
        }
    }

    [Fact]
    public void TryFindConflictingVpn_IgnoresProcessMatchingAllowedPath()
    {
        var directory = Path.Combine(Path.GetTempPath(), "PaqetFire-test-vpn-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        var dummyExe = Path.Combine(directory, "openvpn.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "ping.exe"), dummyExe);

        using var process = Process.Start(new ProcessStartInfo(dummyExe, "-t 127.0.0.1") { CreateNoWindow = true })!;
        try
        {
            var hasConflict = ProcessVpnConflictDetector.Instance.TryFindConflictingVpn(dummyExe, out var conflict);

            if (Process.GetProcessesByName("openvpn").Length == 1 && Process.GetProcessesByName("openvpn-gui").Length == 0)
            {
                Assert.False(hasConflict);
                Assert.Null(conflict);
            }
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill();
                process.WaitForExit();
            }
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch
            {
            }
        }
    }
}
