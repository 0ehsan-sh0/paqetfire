using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace PaqetFire.Broker.Configuration;

/// <summary>Finds usable Windows/Npcap interface settings without invoking PowerShell.</summary>
public sealed class PaqetNetworkDiscovery
{
    public IReadOnlyList<PaqetNetworkCandidate> Discover()
    {
        var candidates = new List<(int Priority, PaqetNetworkCandidate Candidate)>();
        foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (networkInterface.OperationalStatus != OperationalStatus.Up ||
                networkInterface.NetworkInterfaceType is NetworkInterfaceType.Loopback or
                    NetworkInterfaceType.Tunnel ||
                !Guid.TryParse(networkInterface.Id, out var interfaceGuid))
            {
                continue;
            }

            try
            {
                var properties = networkInterface.GetIPProperties();
                var gateway = properties.GatewayAddresses
                    .Select(item => item.Address)
                    .FirstOrDefault(address =>
                        address.AddressFamily == AddressFamily.InterNetwork &&
                        !address.Equals(IPAddress.Any));
                var localAddress = properties.UnicastAddresses
                    .Select(item => item.Address)
                    .FirstOrDefault(address =>
                        address.AddressFamily == AddressFamily.InterNetwork &&
                        !IPAddress.IsLoopback(address) &&
                        !address.Equals(IPAddress.Any) &&
                        !address.GetAddressBytes().Take(2).SequenceEqual(new byte[] { 169, 254 }));

                if (gateway is null || localAddress is null)
                {
                    continue;
                }

                candidates.Add((
                    GetInterfacePriority(networkInterface.NetworkInterfaceType),
                    new PaqetNetworkCandidate(
                        networkInterface.Name,
                        interfaceGuid.ToString("D").ToUpperInvariant(),
                        localAddress.ToString(),
                        gateway.ToString(),
                        ResolveIpv4Mac(gateway, localAddress),
                        networkInterface.Description)));
            }
            catch (NetworkInformationException)
            {
                // An adapter can disappear while Windows is enumerating it.
            }
        }

        return candidates
            .OrderBy(item => item.Priority)
            .ThenBy(item => item.Candidate.InterfaceName, StringComparer.OrdinalIgnoreCase)
            .Select(item => item.Candidate)
            .ToArray();
    }

    private static int GetInterfacePriority(NetworkInterfaceType type) => type switch
    {
        NetworkInterfaceType.Ethernet => 0,
        NetworkInterfaceType.Wireless80211 => 1,
        _ => 2,
    };

    private static string? ResolveIpv4Mac(IPAddress destination, IPAddress source)
    {
        var buffer = new byte[8];
        var length = buffer.Length;
        var result = SendARP(
            BitConverter.ToUInt32(destination.GetAddressBytes(), 0),
            BitConverter.ToUInt32(source.GetAddressBytes(), 0),
            buffer,
            ref length);
        if (result != 0 || length != 6)
        {
            return null;
        }

        return string.Join(':', buffer.Take(length).Select(value => value.ToString("X2")));
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern int SendARP(
        uint destinationIp,
        uint sourceIp,
        [Out] byte[] physicalAddress,
        ref int physicalAddressLength);
}
