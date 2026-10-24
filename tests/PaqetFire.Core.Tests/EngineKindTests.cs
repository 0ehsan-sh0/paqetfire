using PaqetFire.Core.Engines;
using Xunit;

namespace PaqetFire.Core.Tests;

public sealed class EngineKindTests
{
    [Fact]
    public void EngineKind_ContainsOpenVpn()
    {
        Assert.True(Enum.IsDefined(typeof(EngineKind), EngineKind.OpenVpn));
    }
}
