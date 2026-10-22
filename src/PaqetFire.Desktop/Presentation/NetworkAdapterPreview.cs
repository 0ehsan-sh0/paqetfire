using System.Collections.Concurrent;
using System.Net;
using PaqetFire.Core.Network;

namespace PaqetFire.Desktop.Presentation;

public sealed record NetworkInterfaceOption(Guid? InterfaceGuid, string DisplayName);

public sealed record NetworkAdapterPreviewState(
    Guid? SelectedInterfaceGuid,
    IReadOnlyList<NetworkInterfaceOption> Options,
    DetectedNetworkAdapter? Detected,
    IReadOnlyList<NetworkAdapterDetails> Adapters);

/// <summary>
/// Router MACs resolved during this app session, keyed by adapter, gateway, and source address.
/// ARP lookups are slow when they fail, so a resolved answer is reused until the network changes.
/// </summary>
public sealed class GatewayMacCache
{
    private readonly ConcurrentDictionary<string, string> entries = new(StringComparer.Ordinal);

    public static string CreateKey(Guid adapter, IPAddress gateway, IPAddress localAddress) =>
        $"{adapter:D}|{gateway}|{localAddress}";

    public bool TryGet(string key, out string mac) => entries.TryGetValue(key, out mac!);

    public void Store(string key, string? mac)
    {
        if (!string.IsNullOrWhiteSpace(mac))
        {
            entries[key] = mac;
        }
    }

    public string? GetOrResolve(string key, Func<string?> resolve)
    {
        if (TryGet(key, out var cached))
        {
            return cached;
        }

        var resolved = resolve();
        Store(key, resolved);
        return resolved;
    }

    public void Remove(string key) => entries.TryRemove(key, out _);
}

public sealed class NetworkAdapterPreview(NetworkAdapterDetector detector, GatewayMacCache? macCache = null)
{
    /// <summary>Automatic follows the Windows route to the server; Ethernet, then Wi-Fi, is only the fallback.</summary>
    public const string AutomaticOptionLabel = "Automatic (Windows route to the server)";

    private readonly object sync = new();
    private readonly GatewayMacCache macCache = macCache ?? new GatewayMacCache();

    public Guid? SelectedInterfaceGuid { get; private set; }

    public IReadOnlyList<NetworkInterfaceOption> Options { get; private set; } = [];

    public DetectedNetworkAdapter? Detected { get; private set; }

    /// <summary>Enumerates once and optionally resolves the router MAC. Safe to call from a worker thread.</summary>
    /// <param name="refreshRouterMac">Ignore a cached MAC, for an explicit user-requested detection.</param>
    public NetworkAdapterPreviewState Refresh(
        Guid? selectedInterfaceGuid,
        bool resolveRouterMac = true,
        bool refreshRouterMac = false)
    {
        lock (sync)
        {
            SelectedInterfaceGuid = selectedInterfaceGuid;
            Detected = null;
            IReadOnlyList<NetworkAdapterDetails> adapters = [];
            try
            {
                adapters = detector.GetAdapters();
                if (resolveRouterMac)
                {
                    Detected = DetectWithCache(adapters, selectedInterfaceGuid, refreshRouterMac);
                }
            }
            finally
            {
                var options = new List<NetworkInterfaceOption>
                {
                    new(null, AutomaticOptionLabel),
                };
                options.AddRange(adapters.Select(adapter => new NetworkInterfaceOption(
                    adapter.InterfaceGuid, $"{adapter.InterfaceName} ({adapter.LocalAddress})")));
                if (selectedInterfaceGuid is { } selected && adapters.All(adapter => adapter.InterfaceGuid != selected))
                    options.Add(new(selected, $"Unavailable interface ({selected})"));
                Options = options;
            }

            return new(SelectedInterfaceGuid, Options, Detected, adapters);
        }
    }

    private DetectedNetworkAdapter DetectWithCache(
        IReadOnlyList<NetworkAdapterDetails> adapters,
        Guid? selectedInterfaceGuid,
        bool refreshRouterMac)
    {
        // Let the detector choose the adapter (its rules are shared with the broker), but
        // answer the MAC from the session cache when that adapter's gateway is unchanged.
        var cachingDetector = new NetworkAdapterDetector(
            () => adapters,
            (gateway, localAddress) =>
            {
                var adapter = adapters.FirstOrDefault(candidate =>
                    candidate.Gateway.Equals(gateway) && candidate.LocalAddress.Equals(localAddress));
                if (adapter is null)
                {
                    return null;
                }

                var key = GatewayMacCache.CreateKey(adapter.InterfaceGuid, gateway, localAddress);
                if (!refreshRouterMac && macCache.TryGet(key, out var cached))
                {
                    return cached;
                }

                return null;
            });
        var preview = cachingDetector.Detect(adapters, selectedInterfaceGuid);
        if (preview.RouterMac is not null)
        {
            return preview;
        }

        var detected = detector.Detect(adapters, selectedInterfaceGuid);
        macCache.Store(
            GatewayMacCache.CreateKey(detected.Adapter.InterfaceGuid, detected.Adapter.Gateway, detected.Adapter.LocalAddress),
            detected.RouterMac);
        return detected;
    }
}
