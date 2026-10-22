using PaqetFire.Core.Engines;

namespace PaqetFire.Core.Connections;

public enum RouteRecoveryAction
{
    None,
    /// <summary>Restart Paqet alone; Xray and ProxiFyre keep routed traffic failing closed.</summary>
    RestartTransport,
    /// <summary>Start the chain again because the user still wants the route.</summary>
    Reconnect,
    /// <summary>Restore the configured safe state: the kill-switch guard or a full disconnect.</summary>
    SafeDisconnect,
}

/// <param name="Attempt">The attempt the action starts, or the one that is waiting for <paramref name="RetryAt"/>.</param>
public sealed record RouteRecoveryDecision(
    RouteRecoveryAction Action,
    int Attempt = 0,
    DateTimeOffset? RetryAt = null)
{
    public static RouteRecoveryDecision Idle { get; } = new(RouteRecoveryAction.None);

    public bool IsWaiting => Action == RouteRecoveryAction.None && RetryAt is not null;
}

public sealed record RouteRecoveryOptions
{
    /// <summary>Delays before the second and later attempts; the last entry repeats.</summary>
    public IReadOnlyList<TimeSpan> Backoff { get; init; } =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(30),
    ];

    /// <summary>
    /// Attempts without a confirmed healthy tunnel before the route is released to the safe
    /// state. Five attempts span about 18 seconds: long enough to ride out a Wi-Fi roam or
    /// an adapter reset, short enough that a carrier that cannot start does not keep routed
    /// applications blocked indefinitely. A network change or resume re-arms recovery.
    /// </summary>
    public int MaximumConsecutiveFailures { get; init; } = 5;
}

/// <summary>
/// Keeps the route the user asked for. When only Paqet failed it is restarted in place;
/// when the chain is down it is reconnected; both with bounded backoff. Attempts reset
/// only once the tunnel watchdog confirms traffic, so a carrier that starts and then
/// crashes still reaches the limit. Calls must be serialized by the caller.
/// </summary>
public sealed class RouteRecoveryPolicy
{
    private readonly RouteRecoveryOptions options;
    private int attempts;
    private DateTimeOffset? notBefore;

    public RouteRecoveryPolicy(RouteRecoveryOptions? options = null)
    {
        this.options = options ?? new RouteRecoveryOptions();
        if (this.options.Backoff.Count == 0 || this.options.Backoff.Any(delay => delay < TimeSpan.Zero))
        {
            throw new ArgumentException("Recovery backoff needs at least one non-negative delay.", nameof(options));
        }

        if (this.options.MaximumConsecutiveFailures < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "At least one recovery attempt must be allowed.");
        }
    }

    public int Attempts => attempts;

    /// <summary>Recovery stopped after the attempt limit and waits for <see cref="Reset"/>.</summary>
    public bool IsSuspended { get; private set; }

    public DateTimeOffset? LastAttemptAt { get; private set; }

    public DateTimeOffset? NextAttemptAt => notBefore;

    public RouteRecoveryDecision Evaluate(
        DateTimeOffset now,
        bool desiredConnected,
        ConnectionState state,
        bool isKillSwitchEnabled,
        IReadOnlyList<EngineStatus> engines)
    {
        ArgumentNullException.ThrowIfNull(engines);
        if (state is ConnectionState.Connecting or ConnectionState.Disconnecting or ConnectionState.NotReady)
        {
            return RouteRecoveryDecision.Idle;
        }

        var structural = ConnectionRecoveryPolicy.Evaluate(state, isKillSwitchEnabled, engines);
        if (!desiredConnected)
        {
            return structural == ConnectionRecoveryAction.None
                ? RouteRecoveryDecision.Idle
                : new RouteRecoveryDecision(RouteRecoveryAction.SafeDisconnect);
        }

        // A broken policy or capture engine cannot be repaired in place. Restore the safe
        // state first; the next evaluation then reconnects the whole chain.
        if (structural == ConnectionRecoveryAction.SafeRecovery)
        {
            return new RouteRecoveryDecision(RouteRecoveryAction.SafeDisconnect);
        }

        var transportOnly = structural == ConnectionRecoveryAction.RestartTransport;
        if (!transportOnly && state is not (ConnectionState.Disconnected or ConnectionState.Guarded))
        {
            return RouteRecoveryDecision.Idle;
        }

        if (!IsSuspended && attempts >= options.MaximumConsecutiveFailures)
        {
            IsSuspended = true;
            notBefore = null;
        }

        if (IsSuspended)
        {
            return transportOnly
                ? new RouteRecoveryDecision(RouteRecoveryAction.SafeDisconnect)
                : RouteRecoveryDecision.Idle;
        }

        if (notBefore is { } retryAt && now < retryAt)
        {
            return new RouteRecoveryDecision(RouteRecoveryAction.None, attempts + 1, retryAt);
        }

        return new RouteRecoveryDecision(
            transportOnly ? RouteRecoveryAction.RestartTransport : RouteRecoveryAction.Reconnect,
            attempts + 1);
    }

    /// <summary>Records a started attempt. Success is only confirmed later by <see cref="Reset"/>.</summary>
    public void RecordAttempt(DateTimeOffset now)
    {
        attempts++;
        LastAttemptAt = now;
        notBefore = now + options.Backoff[Math.Min(attempts - 1, options.Backoff.Count - 1)];
    }

    /// <summary>Clears the episode after a healthy tunnel, a network change, resume, or a user command.</summary>
    public void Reset()
    {
        attempts = 0;
        notBefore = null;
        IsSuspended = false;
    }
}
