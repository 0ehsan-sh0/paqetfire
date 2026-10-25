namespace PaqetFire.Broker.Tests;

using PaqetFire.Broker.Engines;
using Xunit;

public sealed class OpenVpnLocatorTests
{
    [Fact]
    public void FindExecutable_ReturnsInstalledPath_WhenDefaultExists()
    {
        var tempPayload = Path.Combine(Path.GetTempPath(), "test-payload-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempPayload);
        try
        {
            var found = OpenVpnLocator.FindExecutable(tempPayload);
            if (File.Exists(@"C:\Program Files\OpenVPN\bin\openvpn.exe"))
            {
                Assert.Equal(@"C:\Program Files\OpenVPN\bin\openvpn.exe", found);
            }
        }
        finally
        {
            if (Directory.Exists(tempPayload))
            {
                Directory.Delete(tempPayload, true);
            }
        }
    }

    [Fact]
    public void FindExecutable_PrefersPayload_WhenPresent()
    {
        var tempPayload = Path.Combine(Path.GetTempPath(), "test-payload-" + Guid.NewGuid().ToString("N"));
        var payloadExe = Path.Combine(tempPayload, "engines", "openvpn", "x64", "openvpn.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(payloadExe)!);
        File.WriteAllText(payloadExe, "dummy");
        try
        {
            var found = OpenVpnLocator.FindExecutable(tempPayload);
            Assert.Equal(payloadExe, found);
        }
        finally
        {
            if (Directory.Exists(tempPayload))
            {
                Directory.Delete(tempPayload, true);
            }
        }
    }
}
