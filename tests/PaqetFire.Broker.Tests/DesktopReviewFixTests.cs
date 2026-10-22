using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text.Json;
using PaqetFire.Broker.Ipc;
using PaqetFire.Core.Configuration;
using PaqetFire.Core.Deployment;
using PaqetFire.Core.Ipc;
using PaqetFire.Core.Routing;
using PaqetFire.Desktop.Ipc;
using PaqetFire.Desktop.Presentation;
using Xunit;

namespace PaqetFire.Broker.Tests;

public sealed class DesktopReviewFixTests
{
    [Fact]
    public void SaveIsConfirmedOnlyByTheSubmittedProfile()
    {
        var submitted = new PaqetFireSettings
        {
            ProfileName = "Office",
            ServerEndpoint = "VPN.Example.com:9999",
            TransportKey = "first-key",
            RoutingMode = RoutingMode.AllApplications,
        };
        var stored = PaqetFireSettingsView.FromSettings(submitted with { ServerEndpoint = "vpn.example.com:9999" });

        Assert.True(SaveConfirmation.Reflects(submitted, stored));
        Assert.False(SaveConfirmation.Reflects(submitted, null));
        Assert.False(SaveConfirmation.Reflects(submitted, stored with { ProfileName = "Home" }));
        Assert.False(SaveConfirmation.Reflects(submitted, stored with { ServerEndpoint = "other.example.com:9999" }));
        Assert.False(SaveConfirmation.Reflects(submitted, stored with { HasTransportKey = false }));
        Assert.True(SaveConfirmation.Reflects(submitted with { TransportKey = "" }, stored with { HasTransportKey = true }));
    }

    [Fact]
    public void Ipv6EndpointsCompareCanonically()
    {
        var submitted = new PaqetFireSettings { ServerEndpoint = "[2001:0DB8::0001]:9999" };
        var stored = PaqetFireSettingsView.FromSettings(submitted with { ServerEndpoint = "[2001:db8::1]:9999" });
        Assert.True(SaveConfirmation.Reflects(submitted, stored));
    }

    [Fact]
    public void ARedirectBeforeTheWindowExistsIsDeliveredOnAttach()
    {
        ActivationRedirect.Reset();
        try
        {
            var shown = 0;
            ActivationRedirect.Signal();
            ActivationRedirect.Attach(() => shown++);
            Assert.Equal(1, shown);

            ActivationRedirect.Signal();
            Assert.Equal(2, shown);
        }
        finally
        {
            ActivationRedirect.Reset();
        }
    }

    [Fact]
    public void AttachingWithoutARedirectShowsNothing()
    {
        ActivationRedirect.Reset();
        try
        {
            var shown = 0;
            ActivationRedirect.Attach(() => shown++);
            Assert.Equal(0, shown);
        }
        finally
        {
            ActivationRedirect.Reset();
        }
    }

    [Fact]
    public void AnUnchangedChecklistIsEqualSoTheListIsNotRebuilt()
    {
        var snapshot = new BrokerSnapshot([], false, false, DateTimeOffset.UtcNow, IsConfigured: false,
            Prerequisites: [new PrerequisiteStatus("npcap", "Npcap", false, "Missing")]);
        Assert.Equal(
            SetupChecklist.Create(snapshot, false).Items,
            SetupChecklist.Create(snapshot with { CapturedAt = DateTimeOffset.UtcNow.AddSeconds(15) }, false).Items);
        Assert.NotEqual(
            SetupChecklist.Create(snapshot, false).Items,
            SetupChecklist.Create(snapshot with { IsConfigured = true }, false).Items);
    }

    [Fact]
    public async Task ARequestThatTimesOutWaitingToWriteLeavesOtherRequestsAlone()
    {
        var name = "PaqetFire-test-" + Guid.NewGuid();
        await using var server = new NamedPipeServerStream(
            name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 4096, 4096);
        await using var client = new NamedPipeBrokerClient(name);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var connected = server.WaitForConnectionAsync(deadline.Token);
        await client.OpenAsync(TimeSpan.FromSeconds(10), deadline.Token);
        await connected;
        await using var connection = new NamedPipeBrokerServer.ClientConnection(server, TimeSpan.FromSeconds(5));
        var lost = 0;
        client.ConnectionLost += () => Interlocked.Increment(ref lost);

        // A request is written and waiting for its reply...
        var pending = client.GetSnapshotAsync(TimeSpan.FromSeconds(10), deadline.Token).AsTask();
        var pendingId = await ReadRequestIdAsync(server, deadline.Token);

        // ...while another one gives up before it could start writing.
        var gate = (SemaphoreSlim)typeof(NamedPipeBrokerClient)
            .GetField("writeLock", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(client)!;
        await gate.WaitAsync(deadline.Token);
        try
        {
            await Assert.ThrowsAsync<TimeoutException>(async () =>
                await client.GetLiveStatsAsync(TimeSpan.FromMilliseconds(150), deadline.Token));
        }
        finally
        {
            gate.Release();
        }

        await connection.WriteAsync(BrokerServerMessage.FromResponse(BrokerResponse.Succeeded(
            pendingId, new BrokerSnapshot([], true, false, DateTimeOffset.UtcNow))), deadline.Token);
        Assert.True((await pending).IsRouting);
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
}
