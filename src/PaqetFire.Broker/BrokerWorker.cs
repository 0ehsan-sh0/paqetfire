using PaqetFire.Broker.Deployment;
using PaqetFire.Broker.Ipc;
using PaqetFire.Broker.Runtime;
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
                var paqet = snapshot.Engines.FirstOrDefault(engine => engine.Engine == EngineKind.Paqet);
                var xray = snapshot.Engines.FirstOrDefault(engine => engine.Engine == EngineKind.Xray);
                var proxiFyre = snapshot.Engines.FirstOrDefault(engine => engine.Engine == EngineKind.ProxiFyre);
                var allHealthy = paqet?.State == EngineState.Running &&
                                 xray?.State == EngineState.Running &&
                                 proxiFyre?.State == EngineState.Running;
                var guarded = snapshot.IsKillSwitchEnabled &&
                              paqet?.State == EngineState.Stopped &&
                              xray?.State == EngineState.Stopped &&
                              proxiFyre?.State == EngineState.Running;
                var anyEngineActive = snapshot.Engines.Any(engine =>
                    engine.State is EngineState.Running or EngineState.Starting or EngineState.Faulted);

                if (!allHealthy && !guarded && anyEngineActive)
                {
                    logger.LogWarning(
                        "The engine chain is unhealthy; restoring the configured safe disconnected state.");
                    snapshot = await runtime.DisconnectAsync(stoppingToken).ConfigureAwait(false);
                }

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
}
