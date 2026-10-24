using PaqetFire.Core.Configuration;
using Xunit;

namespace PaqetFire.Core.Tests;

public sealed class UpstreamOpenVpnValidatorTests
{
    [Fact]
    public void DisabledOptions_RequireNoFields()
    {
        var options = new UpstreamOpenVpnOptions { Enabled = false };
        var errors = UpstreamOpenVpnValidator.Validate(options);
        Assert.Empty(errors);
    }

    [Fact]
    public void EnabledOptions_WithoutConfig_FailsValidation()
    {
        var options = new UpstreamOpenVpnOptions { Enabled = true };
        var errors = UpstreamOpenVpnValidator.Validate(options);
        Assert.Contains(errors, e => e.Contains("OpenVPN configuration content is required"));
    }

    [Fact]
    public void EnabledOptions_WithValidConfig_Succeeds()
    {
        var options = new UpstreamOpenVpnOptions
        {
            Enabled = true,
            OvpnConfigText = "client\ndev tun\nproto tcp\nremote 149.88.97.110 443\n",
            Username = "user123",
            Password = "pass123"
        };
        var errors = UpstreamOpenVpnValidator.Validate(options);
        Assert.Empty(errors);
    }

    [Fact]
    public void EnabledOptions_WithoutCredentials_FailsWhenAuthRequired()
    {
        var options = new UpstreamOpenVpnOptions
        {
            Enabled = true,
            OvpnConfigText = "client\ndev tun\nproto tcp\nremote 149.88.97.110 443\nauth-user-pass\n"
        };
        var errors = UpstreamOpenVpnValidator.Validate(options);
        Assert.Contains(errors, e => e.Contains("username is required"));
        Assert.Contains(errors, e => e.Contains("password is required"));
    }

    [Fact]
    public void ParseServerInfo_ExtractsHostPortAndProtocol()
    {
        var sampleOvpn = "client\ndev tun\nproto tcp\nremote 149.88.97.110 443\n";
        var (host, port, proto) = UpstreamOpenVpnValidator.ParseServerInfo(sampleOvpn);
        Assert.Equal("149.88.97.110", host);
        Assert.Equal(443, port);
        Assert.Equal("tcp", proto);
    }

    [Fact]
    public void ParseServerInfo_HandlesCommentsAndWhitespace()
    {
        var sampleOvpn = @"
# Comment line
client
dev tun
proto tcp-client
remote ca-free-6.protonvpn.net 8443
";
        var (host, port, proto) = UpstreamOpenVpnValidator.ParseServerInfo(sampleOvpn);
        Assert.Equal("ca-free-6.protonvpn.net", host);
        Assert.Equal(8443, port);
        Assert.Equal("tcp", proto);
    }
}
