using PaqetFire.Core.Connections;
using PaqetFire.Core.Engines;
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

    private sealed class FakeEngineAdapter(
        EngineKind kind,
        IList<EngineKind>? stopOrder = null) : IEngineAdapter
    {
        public EngineKind Kind { get; } = kind;

        public EngineState State { get; private set; } = EngineState.Stopped;

        public bool FailOnStart { get; init; }

        public int StartCount { get; private set; }

        public ValueTask<EngineStatus> GetStatusAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new EngineStatus(Kind, State));
        }

        public ValueTask StartAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StartCount++;
            if (FailOnStart)
            {
                throw new InvalidOperationException($"{Kind} failed to start.");
            }

            State = EngineState.Running;
            return ValueTask.CompletedTask;
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
