using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using PaqetFire.Core.Connections.Automation;
using PaqetFire.Core.Network;
using PaqetFire.Desktop.Presentation;

namespace PaqetFire.Desktop.Services;

public sealed record NetworkObservation(
    NetworkContext? Context,
    string DisplayName,
    (string Address, string Name)? Hotspot);

/// <summary>
/// Identifies the upstream network for automation rules from a single adapter enumeration.
/// Blocking (adapter enumeration and ARP); call it from a worker thread.
/// </summary>
public sealed class NetworkContextProbe(GatewayMacCache macCache)
{
    public NetworkObservation Observe(string unidentifiedNetworkNonce)
    {
        Windows.Networking.Connectivity.ConnectionProfile? windowsProfile = null;
        try
        {
            windowsProfile = Windows.Networking.Connectivity.NetworkInformation.GetInternetConnectionProfile();
        }
        catch
        {
            // Adapter inspection below still supports a single unambiguous route.
        }

        var interfaces = NetworkInterface.GetAllNetworkInterfaces();
        (string Address, string Name)? hotspot = null;
        try
        {
            hotspot = new HotspotNetworkDetector().TryDetect(interfaces);
        }
        catch (NetworkInformationException)
        {
            // Hotspot detection is advisory; routing decisions still use the upstream network.
        }

        var adapters = new List<(NetworkInterface Adapter, IPInterfaceProperties Properties)>();
        foreach (var candidate in interfaces)
        {
            if (candidate.OperationalStatus != OperationalStatus.Up ||
                candidate.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
            {
                continue;
            }

            try
            {
                var properties = candidate.GetIPProperties();
                if (properties.GatewayAddresses.Any(address => address.Address.AddressFamily == AddressFamily.InterNetwork))
                {
                    adapters.Add((candidate, properties));
                }
            }
            catch (NetworkInformationException)
            {
                // The adapter disappeared while Windows enumerated it.
            }
        }

        var activeAdapterId = windowsProfile?.NetworkAdapter?.NetworkAdapterId;
        var adapter = activeAdapterId is { } windowsAdapterId
            ? adapters.FirstOrDefault(candidate =>
                Guid.TryParse(candidate.Adapter.Id.Trim('{', '}'), out var candidateId) &&
                candidateId == windowsAdapterId)
            : adapters.Count == 1 ? adapters[0] : default;
        if (adapter.Adapter is null)
        {
            // Never apply a trusted-network rule to an arbitrary interface on a
            // multihomed host when Windows cannot identify the active route.
            return new(null, adapters.FirstOrDefault().Adapter?.Name ?? "Current network", hotspot);
        }

        var gateway = adapter.Properties.GatewayAddresses
            .First(address => address.Address.AddressFamily == AddressFamily.InterNetwork).Address;
        var adapterId = adapter.Adapter.Id.Trim('{', '}');
        var windowsProfileName = windowsProfile?.ProfileName?.Trim();
        var localAddress = adapter.Properties.UnicastAddresses
            .Select(value => value.Address)
            .FirstOrDefault(address => address.AddressFamily == AddressFamily.InterNetwork);
        var gatewayMac = ResolveGatewayMac(adapterId, gateway, localAddress);
        var networkSignature = string.Join('|', new[] { windowsProfileName, gatewayMac }
            .Where(value => !string.IsNullOrWhiteSpace(value)));
        if (networkSignature.Length == 0)
        {
            // An ephemeral identity keeps an unidentified network in the safe "unknown"
            // bucket instead of accidentally matching a trusted rule from another LAN.
            networkSignature = $"unidentified:{unidentifiedNetworkNonce}";
        }

        var identity = Encoding.UTF8.GetBytes($"{adapterId}|{gateway}|{networkSignature}");
        var networkId = Convert.ToHexString(SHA256.HashData(identity))[..32];
        var connectivity = NetworkInterface.GetIsNetworkAvailable()
            ? NetworkConnectivity.Internet
            : NetworkConnectivity.Offline;
        try
        {
            connectivity = windowsProfile?.GetNetworkConnectivityLevel() switch
            {
                Windows.Networking.Connectivity.NetworkConnectivityLevel.InternetAccess => NetworkConnectivity.Internet,
                Windows.Networking.Connectivity.NetworkConnectivityLevel.ConstrainedInternetAccess => NetworkConnectivity.CaptivePortal,
                Windows.Networking.Connectivity.NetworkConnectivityLevel.LocalAccess => NetworkConnectivity.LocalOnly,
                _ => NetworkConnectivity.Offline,
            };
        }
        catch
        {
            // The adapter-derived fallback remains useful if Windows connectivity APIs are unavailable.
        }

        var kind = adapter.Adapter.NetworkInterfaceType == NetworkInterfaceType.Wireless80211
            ? NetworkKind.WiFi
            : adapter.Adapter.NetworkInterfaceType == NetworkInterfaceType.Ethernet
                ? NetworkKind.Ethernet
                : NetworkKind.Unknown;

        return new(
            new NetworkContext(networkId, kind, connectivity, IsHostingHotspot: hotspot is not null),
            string.IsNullOrWhiteSpace(windowsProfileName) ? adapter.Adapter.Name : $"{adapter.Adapter.Name} ({windowsProfileName})",
            hotspot);
    }

    private string? ResolveGatewayMac(string adapterId, IPAddress gateway, IPAddress? localAddress)
    {
        if (localAddress is null)
        {
            return null;
        }

        if (!Guid.TryParse(adapterId, out var adapterGuid))
        {
            return NetworkAdapterDetector.ResolveMacAddress(gateway, localAddress);
        }

        return macCache.GetOrResolve(
            GatewayMacCache.CreateKey(adapterGuid, gateway, localAddress),
            () => NetworkAdapterDetector.ResolveMacAddress(gateway, localAddress));
    }
}
