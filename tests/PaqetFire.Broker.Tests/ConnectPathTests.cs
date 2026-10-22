using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using PaqetFire.Broker.Deployment;
using PaqetFire.Broker.Diagnostics;
using PaqetFire.Broker.Engines;
using PaqetFire.Broker.Ipc;
using PaqetFire.Broker.Network;
using PaqetFire.Core.Connections;
using PaqetFire.Core.Deployment;
using PaqetFire.Core.Engines;
using PaqetFire.Core.Ipc;
using PaqetFire.Core.Network;
using PaqetFire.Desktop.Ipc;
using Xunit;

namespace PaqetFire.Broker.Tests;

public sealed class ConnectPathTests
{
    private static readonly NetworkAdapterDetails Ethernet = new(
        Guid.Parse("11111111-1111-1111-1111-111111111111"), "Ethernet",
        IPAddress.Parse("192.168.1.10"), IPAddress.Parse("192.168.1.1"), NetworkInterfaceType.Ethernet)
    {
        InterfaceIndex = 7,
    };

    private static readonly NetworkAdapterDetails Wifi = new(
        Guid.Parse("22222222-2222-2222-2222-222222222222"), "Wi-Fi",
        IPAddress.Parse("10.0.0.20"), IPAddress.Parse("10.0.0.1"), NetworkInterfaceType.Wireless80211)
    {
        InterfaceIndex = 12,
    };

    [Fact]
    public void AutomaticSelectionFollowsTheRouteToTheServer()
    {
        var destinations = new List<IPAddress>();
        var detector = new NetworkAdapterDetector(() => [Ethernet, Wifi], (_, _) => "02:11:22:33:44:55", destination =>
        {
            destinations.Add(destination);
            return 12;
        });

        Assert.Equal("Wi-Fi", detector.Detect(null, IPAddress.Parse("203.0.113.10")).Adapter.InterfaceName);
        // An explicit choice is never overridden by the route.
        Assert.Equal("Ethernet", detector.Detect(Ethernet.InterfaceGuid, IPAddress.Parse("203.0.113.10")).Adapter.InterfaceName);
        // Without a known server, the default route stands in for it.
        Assert.Equal("Wi-Fi", detector.Detect().Adapter.InterfaceName);
        Assert.Equal([IPAddress.Parse("203.0.113.10"), IPAddress.Parse("1.1.1.1")], destinations);
    }

    [Fact]
    public void AutomaticSelectionFallsBackToTheFixedOrderForUnlistedRoutes()
    {
        var detector = new NetworkAdapterDetector(() => [Wifi, Ethernet], (_, _) => "02:11:22:33:44:55", _ => 99);

        Assert.Equal("Ethernet", detector.Detect().Adapter.InterfaceName);
    }

    [Fact]
    public void NeighborRowsYieldTheGatewayMacOnTheMatchingInterface()
    {
        var gateway = IPAddress.Parse("192.168.1.1");
        var row = NeighborRow(gateway, interfaceIndex: 7, [0x02, 0xAA, 0xBB, 0xCC, 0xDD, 0xEE], state: 5);

        Assert.Equal("02:AA:BB:CC:DD:EE", NetworkAdapterDetector.TryParseNeighborRow(row, gateway, 7, out var current));
        Assert.True(current);
        Assert.Null(NetworkAdapterDetector.TryParseNeighborRow(row, gateway, 8, out _));
        Assert.Null(NetworkAdapterDetector.TryParseNeighborRow(row, IPAddress.Parse("192.168.1.2"), 7, out _));

        var stale = NeighborRow(gateway, interfaceIndex: 7, [0x02, 0xAA, 0xBB, 0xCC, 0xDD, 0xEE], state: 4);
        Assert.Equal("02:AA:BB:CC:DD:EE", NetworkAdapterDetector.TryParseNeighborRow(stale, gateway, 0, out current));
        Assert.False(current);

        var incomplete = NeighborRow(gateway, interfaceIndex: 7, [0, 0, 0, 0, 0, 0], state: 1);
        Assert.Null(NetworkAdapterDetector.TryParseNeighborRow(incomplete, gateway, 7, out _));
    }

    [Fact]
    public async Task ServerLookupTakesTheFirstAnswerAndDoesNotWaitForSilentResolvers()
    {
        using var silent = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var answering = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var serving = AnswerOnceAsync(answering, IPAddress.Parse("198.51.100.7"));
        var started = Stopwatch.StartNew();

        var addresses = await DirectDnsResolver.ResolveIpv4Async(
            "server.example",
            [(IPEndPoint)silent.Client.LocalEndPoint!, (IPEndPoint)answering.Client.LocalEndPoint!],
            TimeSpan.FromSeconds(5),
            CancellationToken.None);

        Assert.Equal([IPAddress.Parse("198.51.100.7")], addresses);
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(2));
        await serving;

        started.Restart();
        Assert.Empty(await DirectDnsResolver.ResolveIpv4Async(
            "server.example", [(IPEndPoint)silent.Client.LocalEndPoint!], TimeSpan.FromMilliseconds(300), CancellationToken.None));
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void ProxiFyreReadinessIsReadFromOnlyTheLogWrittenAfterLaunch()
    {
        var directory = Path.Combine(Path.GetTempPath(), "PaqetFire-proxifyre-log-" + Guid.NewGuid());
        var logs = Path.Combine(directory, "logs");
        Directory.CreateDirectory(logs);
        var file = Path.Combine(logs, $"logfile_{DateTime.Now:yyyy-MM-dd}.txt");
        const string marker = "Local Router instance started successfully";
        try
        {
            File.WriteAllText(file, $"earlier run|INFO|[info] SOCKS5 {marker}.\n");
            var tail = new ProxiFyreLogTail(directory);
            Assert.False(tail.Contains(marker));

            File.AppendAllText(file, "now|INFO|ProxiFyre Service is running...\n");
            Assert.False(tail.Contains(marker));
            File.AppendAllText(file, $"now|INFO|[info] SOCKS5 {marker}.\n");
            Assert.True(tail.Contains(marker));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task VerifiedPayloadIsRehashedOnlyWhenAFileChanges()
    {
        var root = Path.Combine(Path.GetTempPath(), "PaqetFire-payload-" + Guid.NewGuid());
        Directory.CreateDirectory(Path.Combine(root, "engines"));
        var file = Path.Combine(root, "engines", "engine.bin");
        File.WriteAllBytes(file, [1, 2, 3, 4]);
        File.WriteAllText(Path.Combine(root, "payload-manifest.json"), $$"""
            {"schemaVersion":"1","architecture":"x64","prerequisites":[],"engines":[
              {"engine":"paqet","version":"test","entryPoint":"engines/engine.bin","files":[
                {"path":"engines/engine.bin","size":4,"sha256":"{{Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)))}}"}]}]}
            """);
        try
        {
            var inspector = new PayloadIntegrityInspector(root);
            Assert.Equal(PayloadInspectionState.Ready, (await inspector.InspectAsync(CancellationToken.None)).State);
            Assert.Equal(PayloadInspectionState.Ready, (await inspector.InspectAsync(CancellationToken.None)).State);

            // Same size, different bytes: the changed identity forces a new hash.
            File.WriteAllBytes(file, [4, 3, 2, 1]);
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(1));
            Assert.Equal(PayloadInspectionState.Invalid, (await inspector.InspectAsync(CancellationToken.None)).State);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RecentTrafficCountsAsAHealthyTunnelWithoutAProbe()
    {
        var probe = new CountingProbe(reachable: false);
        var activity = new TunnelActivity();
        var tracker = new TunnelHealthTracker();
        var watchdog = new TunnelWatchdog(new ConnectedController(), probe, tracker, null!,
            NullLogger<TunnelWatchdog>.Instance, activity: activity);

        activity.RecordLatency(DateTimeOffset.UtcNow);
        await watchdog.CheckOnceAsync(CancellationToken.None);

        Assert.Equal(0, probe.Calls);
        Assert.Equal(TunnelHealthState.Healthy, tracker.Report.State);
    }

    [Fact]
    public async Task AProbeThatTimesOutUnderLoadIsNotAStall()
    {
        var probe = new CountingProbe(reachable: false);
        var stats = new GrowingDownlink();
        var tracker = new TunnelHealthTracker();
        var watchdog = new TunnelWatchdog(new ConnectedController(), probe, tracker, null!,
            NullLogger<TunnelWatchdog>.Instance, trafficStats: stats);

        await watchdog.CheckOnceAsync(CancellationToken.None);

        Assert.Equal(1, probe.Calls);
        Assert.Equal(TunnelHealthState.Healthy, tracker.Report.State);
    }

    [Fact]
    public void DownlinkEvidenceNeedsGrowthNotJustTraffic()
    {
        var activity = new TunnelActivity();
        var now = DateTimeOffset.UtcNow;

        activity.RecordDownlink(1000, now);
        Assert.False(activity.HasEvidenceSince(now.AddSeconds(-1)));
        activity.RecordDownlink(1000, now.AddSeconds(1));
        Assert.False(activity.HasEvidenceSince(now.AddSeconds(-1)));
        activity.RecordDownlink(1500, now.AddSeconds(2));
        Assert.True(activity.HasEvidenceSince(now.AddSeconds(1)));
        // A restarted Xray resets its counters; that only moves the baseline.
        activity.RecordDownlink(10, now.AddSeconds(3));
        Assert.False(activity.HasEvidenceSince(now.AddSeconds(3)));
    }

    [Fact]
    public async Task ADisconnectIsAnsweredWhileAConnectIsStillRunning()
    {
        var name = "PaqetFire-test-" + Guid.NewGuid();
        var handler = new CancellableConnectHandler();
        var server = new NamedPipeBrokerServer(handler, NullLogger<NamedPipeBrokerServer>.Instance, name);
        using var stopping = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var serving = server.RunAsync(stopping.Token);
        try
        {
            await using var client = new NamedPipeBrokerClient(name);
            await client.OpenAsync(TimeSpan.FromSeconds(10), stopping.Token);

            var connect = client.SetConnectionStateAsync(true, TimeSpan.FromSeconds(20), stopping.Token).AsTask();
            await handler.ConnectStarted.Task.WaitAsync(stopping.Token);
            var disconnect = await client.SetConnectionStateAsync(false, TimeSpan.FromSeconds(10), stopping.Token);
            var connected = await connect.WaitAsync(stopping.Token);

            Assert.Equal("disconnected", disconnect.StatusMessage);
            Assert.Equal("connect cancelled", connected.StatusMessage);
        }
        finally
        {
            await stopping.CancelAsync();
            await serving;
        }
    }

    private static byte[] NeighborRow(IPAddress address, int interfaceIndex, byte[] mac, int state)
    {
        var row = new byte[88];
        BinaryPrimitives.WriteUInt16LittleEndian(row, 2);
        address.GetAddressBytes().CopyTo(row, 4);
        BinaryPrimitives.WriteInt32LittleEndian(row.AsSpan(28), interfaceIndex);
        mac.CopyTo(row, 40);
        BinaryPrimitives.WriteInt32LittleEndian(row.AsSpan(72), mac.Length);
        BinaryPrimitives.WriteInt32LittleEndian(row.AsSpan(76), state);
        return row;
    }

    private static async Task AnswerOnceAsync(UdpClient server, IPAddress answer)
    {
        var request = await server.ReceiveAsync();
        var response = request.Buffer.ToList();
        response[2] = 0x81;
        response[3] = 0x80;
        response[7] = 1;
        response.AddRange([0xC0, 0x0C, 0, 1, 0, 1, 0, 0, 0, 60, 0, 4]);
        response.AddRange(answer.GetAddressBytes());
        await server.SendAsync(response.ToArray(), request.RemoteEndPoint);
    }

    private sealed class CountingProbe(bool reachable) : ITunnelProbe
    {
        public int Calls { get; private set; }

        public ValueTask<bool> CheckReachabilityAsync(CancellationToken cancellationToken)
        {
            Calls++;
            return new(reachable);
        }

        public ValueTask<int?> MeasureLatencyAsync(CancellationToken cancellationToken) => new((int?)null);

        public ValueTask<IPAddress?> GetPublicAddressAsync(CancellationToken cancellationToken) => new((IPAddress?)null);
    }

    private sealed class GrowingDownlink : IXrayTrafficStats
    {
        private long downlink = 1000;

        public ValueTask<XrayTrafficCounters?> QueryAsync(CancellationToken cancellationToken)
        {
            downlink += 4096;
            return new(new XrayTrafficCounters(0, downlink));
        }
    }

    private sealed class ConnectedController : IConnectionController
    {
        public ValueTask<ConnectionStatus> GetStatusAsync(CancellationToken cancellationToken) => new(new ConnectionStatus(
            ConnectionState.Connected,
            new EngineStatus(EngineKind.Paqet, EngineState.Running),
            new EngineStatus(EngineKind.Xray, EngineState.Running),
            new EngineStatus(EngineKind.ProxiFyre, EngineState.Running)));

        public ValueTask ConnectAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask GuardAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask DisconnectAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask RestartTransportAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask RestartTransportAsync(bool includePolicy, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    /// <summary>A Connect that runs until a Disconnect cancels it, as the runtime does.</summary>
    private sealed class CancellableConnectHandler : IBrokerRequestHandler
    {
        private readonly CancellationTokenSource connectCancellation = new();

        public TaskCompletionSource ConnectStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<BrokerResponse> HandleAsync(BrokerRequest request, CancellationToken cancellationToken)
        {
            if (request.Command == BrokerCommand.Connect)
            {
                ConnectStarted.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.Infinite, connectCancellation.Token);
                }
                catch (OperationCanceledException)
                {
                    return BrokerResponse.Succeeded(request.RequestId, Snapshot("connect cancelled"));
                }
            }

            await connectCancellation.CancelAsync();
            return BrokerResponse.Succeeded(request.RequestId, Snapshot("disconnected"));
        }

        private static BrokerSnapshot Snapshot(string message) =>
            new([], false, false, DateTimeOffset.UtcNow, StatusMessage: message);
    }
}
