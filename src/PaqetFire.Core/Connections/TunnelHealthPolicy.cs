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

    public TimeSpan InitialRecoveryBackoff { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan MaximumRecoveryBackoff { get; init; } = TimeSpan.FromMinutes(5);
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

    public void Reset() => report = TunnelHealthReport.Unknown;

    public TunnelHealthAction Observe(bool probeSucceeded, DateTimeOffset now)
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
            NextRecoveryAt = now + Backoff(attempts),
        };
        return TunnelHealthAction.RestartTransport;
    }

    public void RecoveryCompleted() => report = report with { State = TunnelHealthState.Stalled };

    private TimeSpan Backoff(int attempts)
    {
        var doublings = Math.Min(attempts - 1, 16);
        var delay = options.InitialRecoveryBackoff * Math.Pow(2, doublings);
        return delay < options.MaximumRecoveryBackoff ? delay : options.MaximumRecoveryBackoff;
    }
}
