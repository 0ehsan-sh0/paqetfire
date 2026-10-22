using System.Net.NetworkInformation;
using PaqetFire.Broker.Runtime;
using PaqetFire.Core.Connections;
using PaqetFire.Core.Ipc;

namespace PaqetFire.Broker.Diagnostics;

/// <summary>Shares the latest tunnel health between the watchdog and broker snapshots.</summary>
public sealed class TunnelHealthTracker
{
    private readonly Lock sync = new();
    private readonly TunnelHealthPolicy policy;
    private readonly SemaphoreSlim probeRequested = new(0, 1);

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

    public TimeSpan NextProbeDelay
    {
        get
        {
            lock (sync)
            {
                return policy.NextProbeDelay;
            }
        }
    }

    public TunnelHealthAction Observe(bool probeSucceeded, DateTimeOffset now, bool networkAvailable = true)
    {
        lock (sync)
        {
            return policy.Observe(probeSucceeded, now, networkAvailable);
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

    public void ResetBackoff()
    {
        lock (sync)
        {
            policy.ResetBackoff();
        }
    }

    /// <summary>Asks the watchdog to probe now, for example right after a connect or restart.</summary>
    public void RequestProbe()
    {
        try
        {
            probeRequested.Release();
        }
        catch (SemaphoreFullException)
        {
            // A probe is already requested.
        }
    }

    internal Task<bool> WaitForProbeRequestAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
        probeRequested.WaitAsync(timeout, cancellationToken);
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
    TimeProvider? timeProvider = null,
    IXrayTrafficStats? trafficStats = null,
    TunnelActivity? activity = null,
    ConnectionProgressTracker? progress = null)
{
    /// <summary>Traffic evidence this recent counts as a successful probe.</summary>
    public static readonly TimeSpan EvidenceWindow = TimeSpan.FromSeconds(5);

    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    public async Task RunAsync(CancellationToken stoppingToken, TimeSpan? interval = null)
    {
        var lastCheckTook = TimeSpan.Zero;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Spacing runs from the start of the previous probe, so a probe that timed
                // out is followed by the next one at once rather than a full interval later.
                var delay = (interval ?? tracker.NextProbeDelay) - lastCheckTook;
                await tracker.WaitForProbeRequestAsync(delay > TimeSpan.Zero ? delay : TimeSpan.Zero, stoppingToken)
                    .ConfigureAwait(false);
                var started = clock.GetTimestamp();
                await CheckOnceAsync(stoppingToken).ConfigureAwait(false);
                lastCheckTook = clock.GetElapsedTime(started);
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
            activity?.Reset();
            return;
        }

        var reachable = await ProbeAsync(cancellationToken).ConfigureAwait(false);
        var previous = tracker.Report.State;
        var action = tracker.Observe(reachable, clock.GetUtcNow(), IsNetworkAvailable());
        progress?.ClearIf(ConnectionStage.ProbingTunnel);
        if (action != TunnelHealthAction.RestartTransport)
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
            // A carrier that failed to start is left Faulted; the broker monitor retries it.
            logger.LogError(exception, "The Paqet transport could not be restarted.");
        }
        finally
        {
            tracker.RecoveryCompleted();
        }
    }

    private async ValueTask<bool> ProbeAsync(CancellationToken cancellationToken)
    {
        // A latency probe or downlink growth seen moments ago already proves the tunnel,
        // without adding probe traffic of our own.
        if (activity?.HasEvidenceSince(clock.GetUtcNow() - EvidenceWindow) == true)
        {
            return true;
        }

        var before = await TryReadDownlinkAsync(cancellationToken).ConfigureAwait(false);
        if (await tunnelProbe.CheckReachabilityAsync(cancellationToken).ConfigureAwait(false))
        {
            return true;
        }

        // Under heavy load a probe can time out on a working tunnel. Bytes that arrived
        // from the server while it waited mean the carrier is alive.
        var after = await TryReadDownlinkAsync(cancellationToken).ConfigureAwait(false);
        if (before is { } start && after is { } end && end > start)
        {
            logger.LogDebug("The tunnel probe failed while traffic still arrived; treating the tunnel as healthy.");
            return true;
        }

        return false;
    }

    private async ValueTask<long?> TryReadDownlinkAsync(CancellationToken cancellationToken)
    {
        if (trafficStats is null)
        {
            return null;
        }

        try
        {
            return (await trafficStats.QueryAsync(cancellationToken).ConfigureAwait(false))?.DownlinkBytes;
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    private static bool IsNetworkAvailable()
    {
        try
        {
            return NetworkInterface.GetIsNetworkAvailable();
        }
        catch (NetworkInformationException)
        {
            return true;
        }
    }
}
