using PaqetFire.Broker.Engines;
using PaqetFire.Core.Engines;
using Xunit;

namespace PaqetFire.Broker.Tests;

public sealed class OpenVpnProcessAdapterTests
{
    [Fact]
    public void Kind_ReturnsOpenVpn()
    {
        var options = new OpenVpnProcessOptions
        {
            ExecutablePath = @"C:\nonexistent\openvpn.exe",
            ConfigurationPath = @"C:\nonexistent\config.ovpn"
        };
        using var adapter = new OpenVpnProcessAdapter(options);
        Assert.Equal(EngineKind.OpenVpn, adapter.Kind);
    }

    [Fact]
    public async Task GetStatusAsync_WhenExecutableDoesNotExist_ReportsNotInstalled()
    {
        var options = new OpenVpnProcessOptions
        {
            ExecutablePath = @"C:\nonexistent\openvpn.exe",
            ConfigurationPath = @"C:\nonexistent\config.ovpn"
        };
        using var adapter = new OpenVpnProcessAdapter(options);
        var status = await adapter.GetStatusAsync(CancellationToken.None);

        Assert.Equal(EngineState.NotInstalled, status.State);
        Assert.Contains("not found", status.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StartAsync_WhenExecutableDoesNotExist_ThrowsFileNotFound()
    {
        var options = new OpenVpnProcessOptions
        {
            ExecutablePath = @"C:\nonexistent\openvpn.exe",
            ConfigurationPath = @"C:\nonexistent\config.ovpn"
        };
        using var adapter = new OpenVpnProcessAdapter(options);

        await Assert.ThrowsAsync<FileNotFoundException>(async () =>
        {
            await adapter.StartAsync(CancellationToken.None);
        });
    }

    [Fact]
    public async Task StartAsync_WhenExecutableDigestMismatches_ThrowsCryptographicException()
    {
        var tempExe = Path.GetTempFileName();
        var tempConfig = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(tempExe, "fake binary");
            var options = new OpenVpnProcessOptions
            {
                ExecutablePath = tempExe,
                ConfigurationPath = tempConfig,
                ExpectedExecutableSha256 = "0000000000000000000000000000000000000000000000000000000000000000"
            };
            using var adapter = new OpenVpnProcessAdapter(options);

            await Assert.ThrowsAsync<System.Security.Cryptography.CryptographicException>(async () =>
            {
                await adapter.StartAsync(CancellationToken.None);
            });
        }
        finally
        {
            File.Delete(tempExe);
            File.Delete(tempConfig);
        }
    }
}
