using PaqetFire.Broker.Deployment;
using Xunit;

namespace PaqetFire.Broker.Tests;

public sealed class PrerequisiteInspectorTests
{
    [Fact]
    public void PrerequisiteInspector_DetectsOpenVpnDcoDriver()
    {
        var inspector = new PrerequisiteInspector();
        var prerequisites = inspector.Inspect();
        var dco = Assert.Single(prerequisites, p => p.Id == "ovpn-dco");
        Assert.Equal("OpenVPN Data Channel Offload driver", dco.DisplayName);
    }
}
