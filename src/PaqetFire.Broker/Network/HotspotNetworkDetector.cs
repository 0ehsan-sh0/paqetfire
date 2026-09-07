namespace PaqetFire.Broker.Network;

public sealed class HotspotNetworkDetector
{
    public static bool IsHotspotAddress(System.Net.IPAddress address)
    {
        var b = address.GetAddressBytes();
        return b[0] == 192 && b[1] == 168 && (b[2] == 137 || b[2] == 173);
    }

    public (string Address, string Name)? TryDetect()
    {
        foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType is System.Net.NetworkInformation.NetworkInterfaceType.Loopback or System.Net.NetworkInformation.NetworkInterfaceType.Tunnel) continue;
            var props = nic.GetIPProperties();
            var ipv4 = props.UnicastAddresses.Select(u => u.Address)
                .FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !System.Net.IPAddress.IsLoopback(a));
            if (ipv4 is null || !IsHotspotAddress(ipv4)) continue;
            var desc = (nic.Description ?? string.Empty) + " " + nic.Name;
            if (desc.Contains("Direct", StringComparison.OrdinalIgnoreCase) || desc.Contains("Virtual", StringComparison.OrdinalIgnoreCase) || desc.Contains("Hotspot", StringComparison.OrdinalIgnoreCase) || desc.Contains("Hosted", StringComparison.OrdinalIgnoreCase) || IsHotspotAddress(ipv4))
                return (ipv4.ToString(), nic.Name);
        }
        return null;
    }
}
