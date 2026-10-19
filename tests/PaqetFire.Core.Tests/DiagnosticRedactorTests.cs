using PaqetFire.Core.Diagnostics;
using Xunit;

namespace PaqetFire.Core.Tests;

public sealed class DiagnosticRedactorTests
{
    [Fact]
    public void SecretsServerAndPublicAddressesAreRedacted()
    {
        const string text = "key=hunter2-transport server vpn.example.net:8443 exit 203.0.113.7 dest 2001:4860:4860::8888 pass hunter2";

        var redacted = DiagnosticRedactor.Redact(text, ["hunter2-transport", "hunter2", null, ""], "vpn.example.net");

        Assert.Equal("key=[secret] server [server]:8443 exit [public-ip] dest [public-ip] pass [secret]", redacted);
    }

    [Theory]
    [InlineData("127.0.0.1:1080")]
    [InlineData("192.168.1.20")]
    [InlineData("10.0.0.1")]
    [InlineData("172.20.1.1")]
    [InlineData("100.64.0.1")]
    [InlineData("198.18.0.5")]
    [InlineData("169.254.1.1")]
    [InlineData("fe80::1")]
    [InlineData("fd00::1")]
    [InlineData("17:32:05.341500")]
    [InlineData("aa:bb:cc:dd:ee:ff")]
    [InlineData("v1.0.0-alpha.21")]
    public void LocalAddressesTimestampsAndVersionsAreKept(string text)
    {
        Assert.Equal(text, DiagnosticRedactor.Redact(text, []));
    }
}
