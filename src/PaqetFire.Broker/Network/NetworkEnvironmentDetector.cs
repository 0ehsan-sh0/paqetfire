using System.Net;
using PaqetFire.Core.Network;

namespace PaqetFire.Broker.Network;

public sealed class NetworkEnvironmentDetector
{
    private readonly NetworkAdapterDetector detector;

    public NetworkEnvironmentDetector() : this(new NetworkAdapterDetector()) { }

    public NetworkEnvironmentDetector(NetworkAdapterDetector detector) => this.detector = detector;

    public NetworkEnvironment Detect(Guid? selectedInterfaceGuid = null) => Detect(selectedInterfaceGuid, null);

    /// <param name="serverAddress">The Paqet server's address when known, so automatic selection follows its route.</param>
    public NetworkEnvironment Detect(Guid? selectedInterfaceGuid, IPAddress? serverAddress)
    {
        var detected = detector.Detect(selectedInterfaceGuid, serverAddress);
        var adapter = detected.Adapter;
        if (string.IsNullOrWhiteSpace(detected.RouterMac))
        {
            throw new InvalidOperationException(
                $"The router MAC address for gateway {adapter.Gateway} on '{adapter.InterfaceName}' could not be detected. " +
                "Verify that the gateway is reachable and try again.");
        }

        return new NetworkEnvironment(adapter.InterfaceName, adapter.InterfaceGuid.ToString("D"),
            adapter.LocalAddress.ToString(), adapter.Gateway.ToString(), detected.RouterMac)
        {
            DnsServers = adapter.DnsServers.Select(server => server.ToString()).ToArray(),
        };
    }
}
