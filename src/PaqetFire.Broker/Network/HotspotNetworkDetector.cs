namespace PaqetFire.Broker.Network;

public sealed class HotspotNetworkDetector : PaqetFire.Core.Network.HotspotNetworkDetector
{
    private readonly Func<(string Address, string Name)?>? detect;

    public HotspotNetworkDetector()
    {
    }

    internal HotspotNetworkDetector(Func<(string Address, string Name)?> detect) => this.detect = detect;

    public override (string Address, string Name)? TryDetect() => detect is null ? base.TryDetect() : detect();
}
