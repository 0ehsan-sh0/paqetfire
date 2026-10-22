using System.Buffers.Binary;
using System.ComponentModel;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace PaqetFire.Core.Network;

public sealed record NetworkAdapterDetails(
    Guid InterfaceGuid,
    string InterfaceName,
    IPAddress LocalAddress,
    IPAddress Gateway,
    NetworkInterfaceType Type)
{
    public IReadOnlyList<IPAddress> DnsServers { get; init; } = [];

    /// <summary>The IPv4 interface index, or 0 when unknown.</summary>
    public int InterfaceIndex { get; init; }
}

public sealed record DetectedNetworkAdapter(NetworkAdapterDetails Adapter, string? RouterMac);

public sealed class NetworkAdapterDetector
{
    // Stands in for the server when its address is not known yet, so automatic selection
    // still follows the Windows default route.
    private static readonly IPAddress DefaultRouteProbe = IPAddress.Parse("1.1.1.1");
    private static readonly TimeSpan ArpRetryDelay = TimeSpan.FromMilliseconds(500);
    private const int ArpAttempts = 3;

    private readonly Func<IReadOnlyList<NetworkAdapterDetails>> readAdapters;
    private readonly Func<IPAddress, IPAddress, string?> resolveMac;
    private readonly Func<IPAddress, int?>? findRouteInterface;

    public NetworkAdapterDetector() : this(ReadAdapters, ResolveMacAddress, FindRouteInterfaceIndex) { }

    public NetworkAdapterDetector(
        Func<IReadOnlyList<NetworkAdapterDetails>> readAdapters,
        Func<IPAddress, IPAddress, string?> resolveMac)
        : this(readAdapters, resolveMac, null)
    {
    }

    /// <param name="findRouteInterface">
    /// Returns the interface index Windows would route a destination through, or null.
    /// Without it, automatic selection uses the Ethernet-then-Wi-Fi order alone.
    /// </param>
    public NetworkAdapterDetector(
        Func<IReadOnlyList<NetworkAdapterDetails>> readAdapters,
        Func<IPAddress, IPAddress, string?> resolveMac,
        Func<IPAddress, int?>? findRouteInterface)
    {
        this.readAdapters = readAdapters;
        this.resolveMac = resolveMac;
        this.findRouteInterface = findRouteInterface;
    }

    public IReadOnlyList<NetworkAdapterDetails> GetAdapters() => readAdapters()
        .OrderBy(adapter => adapter.Type switch
        {
            NetworkInterfaceType.Ethernet => 0,
            NetworkInterfaceType.Wireless80211 => 1,
            _ => 2,
        })
        .ThenBy(adapter => adapter.InterfaceName, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    public DetectedNetworkAdapter Detect(Guid? selectedInterfaceGuid = null) =>
        Detect(GetAdapters(), selectedInterfaceGuid);

    /// <param name="destination">The server address, when known, for automatic selection.</param>
    public DetectedNetworkAdapter Detect(Guid? selectedInterfaceGuid, IPAddress? destination) =>
        Detect(GetAdapters(), selectedInterfaceGuid, destination);

    public DetectedNetworkAdapter Detect(
        IReadOnlyList<NetworkAdapterDetails> adapters,
        Guid? selectedInterfaceGuid) =>
        Detect(adapters, selectedInterfaceGuid, null);

    public DetectedNetworkAdapter Detect(
        IReadOnlyList<NetworkAdapterDetails> adapters,
        Guid? selectedInterfaceGuid,
        IPAddress? destination)
    {
        var adapter = selectedInterfaceGuid is { } selected
            ? adapters.FirstOrDefault(candidate => candidate.InterfaceGuid == selected)
            : SelectAutomatically(adapters, destination);
        if (adapter is null)
        {
            throw new InvalidOperationException(selectedInterfaceGuid is null
                ? "No active IPv4 network adapter with a default gateway was found. Connect Ethernet or Wi-Fi, then detect again."
                : "The selected network interface is unavailable or has no IPv4 default gateway. Reconnect it, select another interface, or choose Automatic.");
        }

        return new DetectedNetworkAdapter(adapter, resolveMac(adapter.Gateway, adapter.LocalAddress));
    }

    private NetworkAdapterDetails? SelectAutomatically(
        IReadOnlyList<NetworkAdapterDetails> adapters,
        IPAddress? destination)
    {
        if (findRouteInterface is not null && adapters.Count > 1)
        {
            // Prefer the adapter Windows itself would use for the server, so Paqet's raw
            // packets leave where the replies come back. Interfaces this detector does not
            // list (VPN tunnels, adapters without a gateway) fall back to the fixed order.
            var target = destination is { AddressFamily: AddressFamily.InterNetwork } ? destination : DefaultRouteProbe;
            if (findRouteInterface(target) is int index && index > 0 &&
                adapters.FirstOrDefault(candidate => candidate.InterfaceIndex == index) is { } routed)
            {
                return routed;
            }
        }

        return adapters.FirstOrDefault();
    }

    private static IReadOnlyList<NetworkAdapterDetails> ReadAdapters()
    {
        var adapters = new List<NetworkAdapterDetails>();
        foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (networkInterface.OperationalStatus != OperationalStatus.Up ||
                networkInterface.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel ||
                !Guid.TryParse(networkInterface.Id, out var guid)) continue;

            try
            {
                var properties = networkInterface.GetIPProperties();
                var local = properties.UnicastAddresses.Select(value => value.Address)
                    .FirstOrDefault(IsUsableLocalIpv4);
                var gateway = properties.GatewayAddresses.Select(value => value.Address)
                    .FirstOrDefault(IsUsableIpv4);
                if (local is not null && gateway is not null)
                    adapters.Add(new(guid, networkInterface.Name, local, gateway, networkInterface.NetworkInterfaceType)
                    {
                        DnsServers = properties.DnsAddresses.Where(IsUsableIpv4).ToArray(),
                        InterfaceIndex = TryGetIpv4Index(properties),
                    });
            }
            catch (NetworkInformationException)
            {
                // An interface may disappear while Windows enumerates it.
            }
        }
        return adapters;
    }

    private static int TryGetIpv4Index(IPInterfaceProperties properties)
    {
        try
        {
            return properties.GetIPv4Properties()?.Index ?? 0;
        }
        catch (NetworkInformationException)
        {
            return 0;
        }
    }

    private static bool IsUsableIpv4(IPAddress address) =>
        address.AddressFamily == AddressFamily.InterNetwork &&
        !IPAddress.IsLoopback(address) && !address.Equals(IPAddress.Any);

    private static bool IsUsableLocalIpv4(IPAddress address) =>
        IsUsableIpv4(address) && address.GetAddressBytes() is not [169, 254, _, _];

    /// <summary>
    /// Finds the gateway's MAC address: a current neighbor-cache entry first, which needs
    /// no traffic, then up to three ARP requests, then a stale cache entry as a last resort.
    /// Blocks for up to about two seconds when the gateway does not answer.
    /// </summary>
    public static string? ResolveMacAddress(IPAddress gateway, IPAddress localAddress)
    {
        if (!IsUsableIpv4(gateway) || !IsUsableIpv4(localAddress)) return null;
        var cached = TryReadNeighborCache(gateway, localAddress, out var current);
        if (cached is not null && current)
        {
            return cached;
        }

        for (var attempt = 1; attempt <= ArpAttempts; attempt++)
        {
            var buffer = new byte[8];
            var length = buffer.Length;
            var result = SendARP(
                BitConverter.ToUInt32(gateway.GetAddressBytes(), 0),
                BitConverter.ToUInt32(localAddress.GetAddressBytes(), 0),
                buffer, ref length);
            if (result == 0 && length == 6)
            {
                return FormatMac(buffer.AsSpan(0, length));
            }

            if (attempt < ArpAttempts)
            {
                Thread.Sleep(ArpRetryDelay);
            }
        }

        return cached;
    }

    /// <summary>
    /// Reads one 88-byte <c>MIB_IPNET_ROW2</c>. Returns the MAC when the row is an IPv4 entry
    /// for <paramref name="address"/> on <paramref name="interfaceIndex"/> (0 matches any
    /// interface); <paramref name="isCurrent"/> reports a reachable or permanent entry.
    /// </summary>
    public static string? TryParseNeighborRow(
        ReadOnlySpan<byte> row,
        IPAddress address,
        int interfaceIndex,
        out bool isCurrent)
    {
        isCurrent = false;
        if (row.Length < NeighborRowSize ||
            BinaryPrimitives.ReadUInt16LittleEndian(row) != AddressFamilyInterNetwork ||
            !row.Slice(4, 4).SequenceEqual(address.GetAddressBytes()))
        {
            return null;
        }

        var rowInterface = BinaryPrimitives.ReadInt32LittleEndian(row[28..]);
        var macLength = BinaryPrimitives.ReadInt32LittleEndian(row[72..]);
        var state = BinaryPrimitives.ReadInt32LittleEndian(row[76..]);
        var mac = row.Slice(40, Math.Clamp(macLength, 0, 32));
        if (interfaceIndex != 0 && rowInterface != interfaceIndex ||
            macLength != 6 || state < NeighborStateProbe || !mac.ContainsAnyExcept((byte)0))
        {
            return null;
        }

        isCurrent = state >= NeighborStateReachable;
        return FormatMac(mac);
    }

    private const int NeighborRowSize = 88;
    private const int NeighborTableHeaderSize = 8;
    private const ushort AddressFamilyInterNetwork = 2;
    private const int NeighborStateProbe = 2;
    private const int NeighborStateReachable = 5;

    private static string? TryReadNeighborCache(IPAddress gateway, IPAddress localAddress, out bool isCurrent)
    {
        isCurrent = false;
        var interfaceIndex = FindInterfaceIndex(localAddress);
        IntPtr table = IntPtr.Zero;
        try
        {
            if (GetIpNetTable2(AddressFamilyInterNetwork, out table) != 0 || table == IntPtr.Zero)
            {
                return null;
            }

            var count = Marshal.ReadInt32(table);
            var row = new byte[NeighborRowSize];
            string? fallback = null;
            for (var index = 0; index < count; index++)
            {
                Marshal.Copy(table + NeighborTableHeaderSize + index * NeighborRowSize, row, 0, row.Length);
                if (TryParseNeighborRow(row, gateway, interfaceIndex, out var current) is { } mac)
                {
                    if (current)
                    {
                        isCurrent = true;
                        return mac;
                    }

                    fallback ??= mac;
                }
            }

            return fallback;
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
        finally
        {
            if (table != IntPtr.Zero)
            {
                FreeMibTable(table);
            }
        }
    }

    private static int FindInterfaceIndex(IPAddress localAddress)
    {
        try
        {
            foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
            {
                var properties = networkInterface.GetIPProperties();
                if (properties.UnicastAddresses.Any(unicast => unicast.Address.Equals(localAddress)))
                {
                    return TryGetIpv4Index(properties);
                }
            }
        }
        catch (NetworkInformationException)
        {
            // Without an index the cache lookup matches the gateway on any interface.
        }

        return 0;
    }

    /// <summary>The interface index Windows routes <paramref name="destination"/> through, or null.</summary>
    public static int? FindRouteInterfaceIndex(IPAddress destination)
    {
        if (destination.AddressFamily != AddressFamily.InterNetwork)
        {
            return null;
        }

        // SOCKADDR_IN: family, port, address, eight bytes of padding.
        var socketAddress = new byte[16];
        BinaryPrimitives.WriteUInt16LittleEndian(socketAddress, AddressFamilyInterNetwork);
        destination.GetAddressBytes().CopyTo(socketAddress, 4);
        try
        {
            return GetBestInterfaceEx(socketAddress, out var index) == 0 && index > 0 ? (int)index : null;
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or Win32Exception)
        {
            return null;
        }
    }

    private static string FormatMac(ReadOnlySpan<byte> mac) =>
        string.Join(':', mac.ToArray().Select(value => value.ToString("X2")));

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern int SendARP(uint destinationAddress, uint sourceAddress,
        [Out] byte[] macAddress, ref int physicalAddressLength);

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern int GetBestInterfaceEx(byte[] destinationAddress, out uint bestInterfaceIndex);

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern int GetIpNetTable2(ushort family, out IntPtr table);

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern void FreeMibTable(IntPtr memory);
}
