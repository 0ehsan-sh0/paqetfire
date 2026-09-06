using PaqetFire.Broker.Deployment;
using PaqetFire.Broker.Ipc;
using PaqetFire.Broker.Runtime;
using PaqetFire.Core.Connections;
using PaqetFire.Core.Engines;
using PaqetFire.Core.Ipc;

namespace PaqetFire.Broker;

public sealed class BrokerWorker(
    ILogger<BrokerWorker> logger,
    PayloadIntegrityInspector payloadInspector,
    IPaqetFireRuntime runtime,
    NamedPipeBrokerServer pipeServer) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var payload = await payloadInspector.InspectAsync(stoppingToken);
        if (payload.State == PayloadInspectionState.Ready)
        {
            logger.LogInformation(
                "PaqetFire Broker started with a verified bundled engine payload.");
        }
        else
        {
            logger.LogWarning(
                "PaqetFire Broker started without an active engine payload: {Detail}",
                payload.Detail);
        }

        await runtime.InitializeAsync(stoppingToken);

        var pipeTask = pipeServer.RunAsync(stoppingToken);
        var monitorTask = MonitorAsync(stoppingToken);
        await Task.WhenAll(pipeTask, monitorTask);

        logger.LogInformation("PaqetFire Broker stopped.");
    }

    private async Task MonitorAsync(CancellationToken stoppingToken)
    {
        BrokerSnapshot? previousSnapshot = null;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(3));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
                {
                    return;
                }

                var snapshot = await runtime.GetSnapshotAsync(stoppingToken).ConfigureAwait(false);
                if (ConnectionRecoveryPolicy.RequiresSafeRecovery(
                        snapshot.ConnectionState,
                        snapshot.IsKillSwitchEnabled,
                        snapshot.Engines))
                {
                    logger.LogWarning(
                        "The engine chain is unhealthy; restoring the configured safe disconnected state.");
                    snapshot = await runtime.DisconnectAsync(stoppingToken).ConfigureAwait(false);
                }

                if (previousSnapshot is not null && HasSamePublishedState(previousSnapshot, snapshot))
                {
                    previousSnapshot = snapshot;
                    continue;
                }

                previousSnapshot = snapshot;

                await pipeServer.PublishAsync(
                        new BrokerEvent(
                            Guid.NewGuid(),
                            IpcProtocol.Version,
                            BrokerEventKind.SnapshotChanged,
                            DateTimeOffset.UtcNow,
                            snapshot),
                        stoppingToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "The connection health monitor failed.");
                previousSnapshot = null;
                await pipeServer.PublishAsync(
                        new BrokerEvent(
                            Guid.NewGuid(),
                            IpcProtocol.Version,
                            BrokerEventKind.Faulted,
                            DateTimeOffset.UtcNow,
                            Error: new BrokerError(
                                BrokerErrorCode.InternalError,
                                "The connection health check failed.")),
                        stoppingToken)
                    .ConfigureAwait(false);
            }
        }
    }

    private static bool HasSamePublishedState(BrokerSnapshot previous, BrokerSnapshot current) =>
        previous.IsRouting == current.IsRouting &&
        previous.IsKillSwitchEnabled == current.IsKillSwitchEnabled &&
        previous.IsConfigured == current.IsConfigured &&
        previous.ConnectionState == current.ConnectionState &&
        string.Equals(previous.StatusMessage, current.StatusMessage, StringComparison.Ordinal) &&
        HasSameSettings(previous.Settings, current.Settings) &&
        previous.Engines.SequenceEqual(current.Engines) &&
        (previous.Prerequisites ?? []).SequenceEqual(current.Prerequisites ?? []) &&
        (previous.RecentLogs ?? []).SequenceEqual(current.RecentLogs ?? []);

    private static bool HasSameSettings(PaqetFire.Core.Configuration.PaqetFireSettingsView? previous,
        PaqetFire.Core.Configuration.PaqetFireSettingsView? current)
    {
        if (ReferenceEquals(previous, current))
        {
            return true;
        }

        if (previous is null || current is null)
        {
            return false;
        }

        return previous.ProfileName == current.ProfileName &&
               previous.ServerEndpoint == current.ServerEndpoint &&
               previous.HasTransportKey == current.HasTransportKey &&
               previous.RoutingMode == current.RoutingMode &&
               previous.BypassLan == current.BypassLan &&
               previous.RouteTcp == current.RouteTcp &&
               previous.RouteUdp == current.RouteUdp &&
               previous.RouteIpv4 == current.RouteIpv4 &&
               previous.RouteIpv6 == current.RouteIpv6 &&
               previous.RegionalPreset == current.RegionalPreset &&
               previous.DomainStrategy == current.DomainStrategy &&
               previous.BlockAds == current.BlockAds &&
               previous.BlockQuic == current.BlockQuic &&
               previous.DirectBitTorrent == current.DirectBitTorrent &&
               previous.KillSwitchEnabled == current.KillSwitchEnabled &&
               previous.ShareWithLan == current.ShareWithLan &&
               previous.LanSocksPort == current.LanSocksPort &&
               previous.LanSocksUsername == current.LanSocksUsername &&
               previous.HasLanSocksPassword == current.HasLanSocksPassword &&
               previous.KcpMode == current.KcpMode &&
               previous.SelectedApplications.SequenceEqual(current.SelectedApplications) &&
               previous.UserExclusions.SequenceEqual(current.UserExclusions) &&
               previous.LocalTcpFlags.SequenceEqual(current.LocalTcpFlags) &&
               previous.RemoteTcpFlags.SequenceEqual(current.RemoteTcpFlags);
    }
}
