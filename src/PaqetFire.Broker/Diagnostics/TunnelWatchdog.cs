using PaqetFire.Broker.Runtime;
using PaqetFire.Core.Connections;

namespace PaqetFire.Broker.Diagnostics;

/// <summary>Shares the latest tunnel health between the watchdog and broker snapshots.</summary>
public sealed class TunnelHealthTracker
{
    private readonly Lock sync = new();
    private readonly TunnelHealthPolicy policy;

    public TunnelHealthTracker(TunnelHealthOptions? options = null) => policy = new TunnelHealthPolicy(options);

    public TunnelHealthReport Report
    {
        get
        {
            lock (sync)
            {
                return policy.Report;
            }
        }
    }

    public TunnelHealthAction Observe(bool probeSucceeded, DateTimeOffset now)
    {
        lock (sync)
        {
            return policy.Observe(probeSucceeded, now);
        }
    }

    public void RecoveryCompleted()
    {
        lock (sync)
        {
            policy.RecoveryCompleted();
        }
    }

    public void Reset()
    {
        lock (sync)
        {
            policy.Reset();
        }
    }
}

/// <summary>
/// Paqet keeps running and retrying when its server stops answering, so process state
/// alone reports a dead route as Connected. This loop probes the carrier itself and
/// restarts the transport with backoff when it stays unreachable.
/// </summary>
public sealed class TunnelWatchdog(
    IConnectionController connectionController,
    ITunnelProbe tunnelProbe,
    TunnelHealthTracker tracker,
    IPaqetFireRuntime runtime,
    ILogger<TunnelWatchdog> logger,
    TimeProvider? timeProvider = null)
{
    public static readonly TimeSpan ProbeInterval = TimeSpan.FromSeconds(10);

    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    public async Task RunAsync(CancellationToken stoppingToken, TimeSpan? interval = null)
    {
        using var timer = new PeriodicTimer(interval ?? ProbeInterval, clock);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await CheckOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "The tunnel watchdog check failed.");
            }
        }
    }

    internal async Task CheckOnceAsync(CancellationToken cancellationToken)
    {
        var status = await connectionController.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        if (status.State != ConnectionState.Connected)
        {
            tracker.Reset();
            return;
        }

        var reachable = await tunnelProbe.CheckReachabilityAsync(cancellationToken).ConfigureAwait(false);
        var previous = tracker.Report.State;
        if (tracker.Observe(reachable, clock.GetUtcNow()) != TunnelHealthAction.RestartTransport)
        {
            if (reachable && previous is TunnelHealthState.Stalled or TunnelHealthState.Recovering)
            {
                logger.LogInformation("The Paqet tunnel is reachable again.");
            }

            return;
        }

        var report = tracker.Report;
        logger.LogWarning(
            "The Paqet tunnel did not answer {Failures} consecutive probes; restarting the transport (attempt {Attempt}).",
            report.ConsecutiveFailures,
            report.RecoveryAttempts);
        try
        {
            await runtime.RestartTransportAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "The Paqet transport could not be restarted.");
        }
        finally
        {
            tracker.RecoveryCompleted();
        }
    }
}
