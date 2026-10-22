namespace PaqetFire.Core.Connections;

public enum TunnelHealthState
{
    Unknown,
    Healthy,
    Stalled,
    Recovering,
}

public sealed record TunnelHealthReport(
    TunnelHealthState State,
    DateTimeOffset? LastSuccessAt,
    int ConsecutiveFailures,
    int RecoveryAttempts,
    DateTimeOffset? NextRecoveryAt)
{
    public static TunnelHealthReport Unknown { get; } = new(TunnelHealthState.Unknown, null, 0, 0, null);
}

public enum TunnelHealthAction
{
    None,
    RestartTransport,
}

public sealed record TunnelHealthOptions
{
    public int FailuresBeforeStalled { get; init; } = 3;

    /// <summary>Probe spacing while the tunnel answers.</summary>
    public TimeSpan ProbeInterval { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Probe spacing after a failure, so a stall is acted on within about three failed
    /// probes rather than three full intervals.
    /// </summary>
    public TimeSpan FailedProbeInterval { get; init; } = TimeSpan.FromSeconds(3);

    public TimeSpan InitialRecoveryBackoff { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Backoff cap while a network adapter is up, when a restart can plausibly help.</summary>
    public TimeSpan MaximumRecoveryBackoff { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Backoff cap while Windows reports no usable network.</summary>
    public TimeSpan OfflineMaximumRecoveryBackoff { get; init; } = TimeSpan.FromMinutes(5);
}

/// <summary>
/// Decides when a running engine chain whose tunnel no longer carries traffic needs a
/// transport restart. Engine process health is handled separately by the broker monitor;
/// this policy only sees probe results while the chain reports Connected.
/// </summary>
public sealed class TunnelHealthPolicy(TunnelHealthOptions? options = null)
{
    private readonly TunnelHealthOptions options = options ?? new();
    private TunnelHealthReport report = TunnelHealthReport.Unknown;

    public TunnelHealthReport Report => report;

    /// <summary>How long to wait before the next probe.</summary>
    public TimeSpan NextProbeDelay => report.ConsecutiveFailures > 0 ? options.FailedProbeInterval : options.ProbeInterval;

    public void Reset() => report = TunnelHealthReport.Unknown;

    /// <summary>
    /// Forgets the restart backoff without forgetting failures. A new network makes a
    /// restart likely to help again, so the next stall must not wait out the old backoff.
    /// </summary>
    public void ResetBackoff() => report = report with { RecoveryAttempts = 0, NextRecoveryAt = null };

    public TunnelHealthAction Observe(bool probeSucceeded, DateTimeOffset now, bool networkAvailable = true)
    {
        if (probeSucceeded)
        {
            report = new TunnelHealthReport(TunnelHealthState.Healthy, now, 0, 0, null);
            return TunnelHealthAction.None;
        }

        var failures = report.ConsecutiveFailures + 1;
        if (failures < options.FailuresBeforeStalled)
        {
            report = report with { ConsecutiveFailures = failures };
            return TunnelHealthAction.None;
        }

        if (report.NextRecoveryAt is { } scheduled && now < scheduled)
        {
            report = report with { State = TunnelHealthState.Stalled, ConsecutiveFailures = failures };
            return TunnelHealthAction.None;
        }

        var attempts = report.RecoveryAttempts + 1;
        report = report with
        {
            State = TunnelHealthState.Recovering,
            ConsecutiveFailures = failures,
            RecoveryAttempts = attempts,
            NextRecoveryAt = now + Backoff(attempts, networkAvailable),
        };
        return TunnelHealthAction.RestartTransport;
    }

    public void RecoveryCompleted() => report = report with { State = TunnelHealthState.Stalled };

    private TimeSpan Backoff(int attempts, bool networkAvailable)
    {
        var doublings = Math.Min(attempts - 1, 16);
        var delay = options.InitialRecoveryBackoff * Math.Pow(2, doublings);
        var maximum = networkAvailable ? options.MaximumRecoveryBackoff : options.OfflineMaximumRecoveryBackoff;
        return delay < maximum ? delay : maximum;
    }
}
