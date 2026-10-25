using PaqetFire.Core.Configuration;
using Xunit;

namespace PaqetFire.Core.Tests;

public sealed class OpenVpnConfigurationWriterTests
{
    [Fact]
    public void GenerateRuntimeConfig_InjectsSocksProxyAndWintunAdapter()
    {
        var writer = new OpenVpnConfigurationWriter();
        var original = "client\ndev tun\nproto tcp\nremote 149.88.97.110 443\n";
        var generated = writer.GenerateRuntimeConfig(original, 1080, @"C:\data\auth.txt", "PaqetFire-VPN");

        Assert.Contains("socks-proxy 127.0.0.1 1080", generated);
        Assert.Contains("windows-driver wintun", generated);
        Assert.Contains("dev-type tun", generated);
        Assert.Contains("dev PaqetFire-VPN", generated);
        Assert.Contains(@"auth-user-pass ""C:\data\auth.txt""", generated);
    }

    [Fact]
    public void GenerateRuntimeConfig_ReplacesExistingAuthUserPassAndDev()
    {
        var writer = new OpenVpnConfigurationWriter();
        var original = @"
client
dev tun
proto tcp
remote 149.88.97.110 443
auth-user-pass old-auth.txt
dev custom_tap
socks-proxy 192.168.1.1 9999
";
        var generated = writer.GenerateRuntimeConfig(original, 1081, @"C:\sec\auth.txt", "PaqetFire-VPN");

        Assert.DoesNotContain("old-auth.txt", generated);
        Assert.DoesNotContain("custom_tap", generated);
        Assert.DoesNotContain("192.168.1.1 9999", generated);
        Assert.Contains("socks-proxy 127.0.0.1 1081", generated);
        Assert.Contains(@"auth-user-pass ""C:\sec\auth.txt""", generated);
        Assert.Contains("dev PaqetFire-VPN", generated);
    }

    [Fact]
    public void GenerateRuntimeConfig_InjectsSocksAuthFile_WhenProvided()
    {
        var original = "client\ndev tun\nremote 149.88.97.110 443\n";
        var writer = new OpenVpnConfigurationWriter();
        var result = writer.GenerateRuntimeConfig(
            original,
            1080,
            @"C:\ProgramData\PaqetFire\openvpn\proton-auth.txt",
            @"C:\ProgramData\PaqetFire\openvpn\socks-auth.txt",
            "PaqetFire-VPN");

        Assert.Contains("socks-proxy 127.0.0.1 1080 \"C:\\ProgramData\\PaqetFire\\openvpn\\socks-auth.txt\"", result);
        Assert.Contains("auth-user-pass \"C:\\ProgramData\\PaqetFire\\openvpn\\proton-auth.txt\"", result);
    }
}
