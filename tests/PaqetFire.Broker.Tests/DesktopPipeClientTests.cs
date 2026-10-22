using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text.Json;
using PaqetFire.Broker.Ipc;
using PaqetFire.Core.Ipc;
using PaqetFire.Desktop.Ipc;
using Xunit;

namespace PaqetFire.Broker.Tests;

public sealed class DesktopPipeClientTests
{
    [Fact]
    public async Task UnansweredRequestTimesOutWithoutResettingThePipe()
    {
        var name = "PaqetFire-test-" + Guid.NewGuid();
        await using var server = CreateServer(name);
        await using var client = new NamedPipeBrokerClient(name);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var connected = server.WaitForConnectionAsync(deadline.Token);
        await client.OpenAsync(TimeSpan.FromSeconds(10), deadline.Token);
        await connected;
        await using var connection = new NamedPipeBrokerServer.ClientConnection(server, TimeSpan.FromSeconds(5));
        var lost = 0;
        client.ConnectionLost += () => Interlocked.Increment(ref lost);

        // The whole frame is written; only the reply is late (a slow stats request).
        var slow = client.GetLiveStatsAsync(TimeSpan.FromMilliseconds(150), deadline.Token).AsTask();
        var slowId = await ReadRequestIdAsync(server, deadline.Token);
        await Assert.ThrowsAsync<TimeoutException>(() => slow);

        var next = client.GetSnapshotAsync(TimeSpan.FromSeconds(5), deadline.Token).AsTask();
        var nextId = await ReadRequestIdAsync(server, deadline.Token);
        // A late reply to the abandoned request is ignored rather than corrupting the stream.
        await connection.WriteAsync(BrokerServerMessage.FromResponse(BrokerResponse.Succeeded(
            slowId, new BrokerSnapshot([], false, false, DateTimeOffset.UtcNow))), deadline.Token);
        await connection.WriteAsync(BrokerServerMessage.FromResponse(BrokerResponse.Succeeded(
            nextId, new BrokerSnapshot([], true, false, DateTimeOffset.UtcNow))), deadline.Token);

        Assert.True((await next).IsRouting);
        Assert.Equal(0, Volatile.Read(ref lost));
    }

    [Fact]
    public async Task BrokerClosingThePipeRaisesConnectionLost()
    {
        var name = "PaqetFire-test-" + Guid.NewGuid();
        var server = CreateServer(name);
        await using var client = new NamedPipeBrokerClient(name);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var connected = server.WaitForConnectionAsync(deadline.Token);
        await client.OpenAsync(TimeSpan.FromSeconds(10), deadline.Token);
        await connected;
        var lost = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ConnectionLost += () => lost.TrySetResult();

        await server.DisposeAsync();

        await lost.Task.WaitAsync(deadline.Token);
    }

    [Fact]
    public async Task ClosingTheClientIsNotReportedAsALostConnection()
    {
        var name = "PaqetFire-test-" + Guid.NewGuid();
        await using var server = CreateServer(name);
        await using var client = new NamedPipeBrokerClient(name);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var connected = server.WaitForConnectionAsync(deadline.Token);
        await client.OpenAsync(TimeSpan.FromSeconds(10), deadline.Token);
        await connected;
        var lost = 0;
        client.ConnectionLost += () => Interlocked.Increment(ref lost);

        await client.CloseAsync(deadline.Token);
        await Task.Delay(100);

        Assert.Equal(0, Volatile.Read(ref lost));
    }

    private static async Task<Guid> ReadRequestIdAsync(NamedPipeServerStream server, CancellationToken cancellationToken)
    {
        var prefix = new byte[4];
        await server.ReadExactlyAsync(prefix, cancellationToken);
        var payload = new byte[BinaryPrimitives.ReadInt32LittleEndian(prefix)];
        await server.ReadExactlyAsync(payload, cancellationToken);
        using var document = JsonDocument.Parse(payload);
        return document.RootElement.GetProperty("requestId").GetGuid();
    }

    private static NamedPipeServerStream CreateServer(string name) => new(
        name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous,
        4096, 4096);
}
