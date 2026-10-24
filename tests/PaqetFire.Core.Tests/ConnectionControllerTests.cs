using PaqetFire.Core.Connections;
using PaqetFire.Core.Engines;
using PaqetFire.Core.Ipc;
using Xunit;

namespace PaqetFire.Core.Tests;

public sealed class ConnectionControllerTests
{
    [Fact]
    public async Task Guard_StartsRoutingAndStopsCarrierEngines()
    {
        var paqet = new FakeEngineAdapter(EngineKind.Paqet);
        var xray = new FakeEngineAdapter(EngineKind.Xray);
        var proxiFyre = new FakeEngineAdapter(EngineKind.ProxiFyre);
        using var controller = new ConnectionController(paqet, xray, proxiFyre);

        await controller.GuardAsync(CancellationToken.None);

        var status = await controller.GetStatusAsync(CancellationToken.None);
        Assert.Equal(ConnectionState.Guarded, status.State);
        Assert.Equal(EngineState.Stopped, status.Paqet.State);
        Assert.Equal(EngineState.Stopped, status.Xray.State);
        Assert.Equal(EngineState.Running, status.ProxiFyre.State);
        Assert.Equal(0, paqet.StartCount);
        Assert.Equal(0, xray.StartCount);
        Assert.Equal(1, proxiFyre.StartCount);
    }

    [Fact]
    public async Task Connect_FromGuard_StartsCarrierAndKeepsRoutingActive()
    {
        var paqet = new FakeEngineAdapter(EngineKind.Paqet);
        var xray = new FakeEngineAdapter(EngineKind.Xray);
        var proxiFyre = new FakeEngineAdapter(EngineKind.ProxiFyre);
        using var controller = new ConnectionController(paqet, xray, proxiFyre);

        await controller.GuardAsync(CancellationToken.None);
        await controller.ConnectAsync(CancellationToken.None);

        var status = await controller.GetStatusAsync(CancellationToken.None);
        Assert.Equal(ConnectionState.Connected, status.State);
        Assert.Equal(EngineState.Running, status.Paqet.State);
        Assert.Equal(EngineState.Running, status.Xray.State);
        Assert.Equal(EngineState.Running, status.ProxiFyre.State);
        Assert.Equal(1, proxiFyre.StartCount);
    }

    [Fact]
    public async Task FailedConnect_FromGuard_LeavesRoutingGuardInPlace()
    {
        var paqet = new FakeEngineAdapter(EngineKind.Paqet);
        var xray = new FakeEngineAdapter(EngineKind.Xray) { FailOnStart = true };
        var proxiFyre = new FakeEngineAdapter(EngineKind.ProxiFyre);
        using var controller = new ConnectionController(paqet, xray, proxiFyre);

        await controller.GuardAsync(CancellationToken.None);

        await Assert.ThrowsAsync<ConnectionTransitionException>(async () =>
            await controller.ConnectAsync(CancellationToken.None));

        var status = await controller.GetStatusAsync(CancellationToken.None);
        Assert.Equal(ConnectionState.Guarded, status.State);
        Assert.Equal(EngineState.Stopped, status.Paqet.State);
        Assert.Equal(EngineState.Stopped, status.Xray.State);
        Assert.Equal(EngineState.Running, status.ProxiFyre.State);
    }

    [Fact]
    public async Task Disconnect_FromConnected_StopsRoutingBeforeCarriers()
    {
        var stopOrder = new List<EngineKind>();
        var paqet = new FakeEngineAdapter(EngineKind.Paqet, stopOrder);
        var xray = new FakeEngineAdapter(EngineKind.Xray, stopOrder);
        var proxiFyre = new FakeEngineAdapter(EngineKind.ProxiFyre, stopOrder);
        using var controller = new ConnectionController(paqet, xray, proxiFyre);

        await controller.ConnectAsync(CancellationToken.None);
        await controller.DisconnectAsync(CancellationToken.None);

        var status = await controller.GetStatusAsync(CancellationToken.None);
        Assert.Equal(ConnectionState.Disconnected, status.State);
        Assert.Equal(
            [EngineKind.ProxiFyre, EngineKind.Xray, EngineKind.Paqet],
            stopOrder);
    }

    [Fact]
    public async Task Connect_StartsPaqetAndXrayTogetherAndProxiFyreLast()
    {
        var events = new List<string>();
        var paqetGate = new TaskCompletionSource();
        var paqet = new FakeEngineAdapter(EngineKind.Paqet) { Events = events, StartGate = paqetGate.Task };
        var xray = new FakeEngineAdapter(EngineKind.Xray) { Events = events };
        var proxiFyre = new FakeEngineAdapter(EngineKind.ProxiFyre) { Events = events };
        using var controller = new ConnectionController(paqet, xray, proxiFyre);
        var stages = new List<ConnectionStage>();

        var connecting = controller.ConnectAsync(stages.Add, CancellationToken.None).AsTask();
        // Xray finished while Paqet was still starting; capture waits for both.
        Assert.Equal(["start Paqet", "start Xray", "started Xray"], events);
        paqetGate.SetResult();
        await connecting;

        Assert.Equal(
            ["start Paqet", "start Xray", "started Xray", "started Paqet", "start ProxiFyre", "started ProxiFyre"],
            events);
        Assert.Equal(
            [ConnectionStage.StartingTransport, ConnectionStage.StartingPolicy, ConnectionStage.StartingCapture],
            stages);
    }

    [Fact]
    public async Task FailedCarrierCancelsItsSiblingAndReportsTheRootCause()
    {
        var paqet = new FakeEngineAdapter(EngineKind.Paqet) { FailOnStart = true };
        var xray = new FakeEngineAdapter(EngineKind.Xray) { StartGate = Task.Delay(Timeout.Infinite) };
        var proxiFyre = new FakeEngineAdapter(EngineKind.ProxiFyre);
        using var controller = new ConnectionController(paqet, xray, proxiFyre);

        var error = await Assert.ThrowsAsync<ConnectionTransitionException>(async () =>
            await controller.ConnectAsync(CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Equal(EngineKind.Paqet, error.Engine);
        Assert.IsType<InvalidOperationException>(error.InnerException);
        Assert.Equal(0, proxiFyre.StartCount);
        Assert.Equal(ConnectionState.Disconnected, (await controller.GetStatusAsync(CancellationToken.None)).State);
    }

    [Fact]
    public async Task TransportRestartWithPolicyRestartsXrayButNeverProxiFyre()
    {
        var paqet = new FakeEngineAdapter(EngineKind.Paqet);
        var xray = new FakeEngineAdapter(EngineKind.Xray);
        var proxiFyre = new FakeEngineAdapter(EngineKind.ProxiFyre);
        using var controller = new ConnectionController(paqet, xray, proxiFyre);
        await controller.ConnectAsync(CancellationToken.None);

        await controller.RestartTransportAsync(includePolicy: true, CancellationToken.None);
        await controller.RestartTransportAsync(includePolicy: false, CancellationToken.None);

        Assert.Equal(3, paqet.StartCount);
        Assert.Equal(2, xray.StartCount);
        Assert.Equal(1, proxiFyre.StartCount);
        Assert.Equal(ConnectionState.Connected, (await controller.GetStatusAsync(CancellationToken.None)).State);
    }

    [Fact]
    public async Task ConnectAndDisconnect_WithOpenVpn_ExecutesInCorrectOrder()
    {
        var events = new List<string>();
        var stopOrder = new List<EngineKind>();
        var paqet = new FakeEngineAdapter(EngineKind.Paqet) { Events = events };
        var xray = new FakeEngineAdapter(EngineKind.Xray) { Events = events };
        var openVpn = new FakeEngineAdapter(EngineKind.OpenVpn, stopOrder) { Events = events };
        var proxiFyre = new FakeEngineAdapter(EngineKind.ProxiFyre, stopOrder) { Events = events };

        using var controller = new ConnectionController(paqet, xray, proxiFyre, openVpn);
        await controller.ConnectAsync(CancellationToken.None);

        var status = await controller.GetStatusAsync(CancellationToken.None);
        Assert.Equal(ConnectionState.Connected, status.State);
        Assert.NotNull(status.OpenVpn);
        Assert.Equal(EngineState.Running, status.OpenVpn.State);

        var openVpnStartIndex = events.IndexOf("start OpenVpn");
        var proxiFyreStartIndex = events.IndexOf("start ProxiFyre");
        Assert.True(openVpnStartIndex < proxiFyreStartIndex);

        await controller.DisconnectAsync(CancellationToken.None);
        Assert.Equal(EngineKind.ProxiFyre, stopOrder[0]);
        Assert.Equal(EngineKind.OpenVpn, stopOrder[1]);
    }
    private sealed class FakeEngineAdapter(
        EngineKind kind,
        IList<EngineKind>? stopOrder = null) : IEngineAdapter
    {
        public EngineKind Kind { get; } = kind;

        public EngineState State { get; private set; } = EngineState.Stopped;

        public bool FailOnStart { get; init; }

        public int StartCount { get; private set; }

        public List<string>? Events { get; init; }

        public Task? StartGate { get; init; }

        public ValueTask<EngineStatus> GetStatusAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new EngineStatus(Kind, State));
        }

        public async ValueTask StartAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StartCount++;
            Events?.Add($"start {Kind}");
            if (FailOnStart)
            {
                throw new InvalidOperationException($"{Kind} failed to start.");
            }

            if (StartGate is not null)
            {
                await StartGate.WaitAsync(cancellationToken);
            }

            State = EngineState.Running;
            Events?.Add($"started {Kind}");
        }

        public ValueTask StopAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            State = EngineState.Stopped;
            stopOrder?.Add(Kind);
            return ValueTask.CompletedTask;
        }
    }
}
