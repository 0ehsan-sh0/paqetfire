using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace PaqetFire.Broker.Network;

public sealed class NetworkEnvironmentDetector
{
    public NetworkEnvironment Detect()
    {
        var candidate = NetworkInterface.GetAllNetworkInterfaces()
            .Where(networkInterface =>
                networkInterface.OperationalStatus == OperationalStatus.Up &&
                networkInterface.NetworkInterfaceType is not (
                    NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
            .Select(TryCreateCandidate)
            .Where(value => value is not null)
            .OrderBy(value => value!.Priority)
            .FirstOrDefault()
            ?? throw new InvalidOperationException(
                "No active IPv4 network adapter with a default gateway was found.");

        var mac = ResolveMacAddress(candidate.Gateway);
        if (string.IsNullOrEmpty(mac))
        {
            throw new InvalidOperationException(
                $"The router MAC address for gateway {candidate.Gateway} could not be detected. " +
                "Verify that the gateway is reachable and try again.");
        }

        return new NetworkEnvironment(
            candidate.Interface.Name,
            NormalizeInterfaceGuid(candidate.Interface.Id),
            candidate.LocalAddress.ToString(),
            candidate.Gateway.ToString(),
            mac);
    }

    private static Candidate? TryCreateCandidate(NetworkInterface networkInterface)
    {
        try
        {
            var properties = networkInterface.GetIPProperties();
            var local = properties.UnicastAddresses
                .Select(value => value.Address)
                .FirstOrDefault(address =>
                    address.AddressFamily == AddressFamily.InterNetwork &&
                    !IPAddress.IsLoopback(address) &&
                    !address.Equals(IPAddress.Any));
            var gateway = properties.GatewayAddresses
                .Select(value => value.Address)
                .FirstOrDefault(address =>
                    address.AddressFamily == AddressFamily.InterNetwork &&
                    !address.Equals(IPAddress.Any));

            if (local is null || gateway is null)
            {
                return null;
            }

            var priority = networkInterface.NetworkInterfaceType switch
            {
                NetworkInterfaceType.Ethernet => 0,
                NetworkInterfaceType.Wireless80211 => 1,
                _ => 2,
            };
            return new Candidate(networkInterface, local, gateway, priority);
        }
        catch (NetworkInformationException)
        {
            return null;
        }
    }

    private static string NormalizeInterfaceGuid(string id)
    {
        if (!Guid.TryParse(id.Trim().Trim('{', '}'), out var guid))
        {
            throw new InvalidOperationException("The active network adapter has no valid GUID.");
        }

        return guid.ToString("D");
    }

    private static string ResolveMacAddress(IPAddress gateway)
    {
        var destination = BitConverter.ToUInt32(gateway.GetAddressBytes(), 0);
        var buffer = new byte[6];
        var length = buffer.Length;
        var result = SendARP(destination, 0, buffer, ref length);
        return result == 0 && length == 6
            ? string.Join(':', buffer.Take(length).Select(value => value.ToString("x2")))
            : string.Empty;
    }

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern int SendARP(
        uint destinationAddress,
        uint sourceAddress,
        byte[] macAddress,
        ref int physicalAddressLength);

    private sealed record Candidate(
        NetworkInterface Interface,
        IPAddress LocalAddress,
        IPAddress Gateway,
        int Priority);
}
