using PaqetFire.Core.Ipc;

namespace PaqetFire.Broker.Runtime;

/// <summary>
/// The stage of the connect or recovery currently running, published in broker snapshots.
/// <see cref="Changed"/> fires on every transition so the monitor can publish immediately.
/// </summary>
public sealed class ConnectionProgressTracker(TimeProvider? timeProvider = null)
{
    // The tunnel probe after a connect normally clears this within seconds; the cap keeps
    // a lost probe from leaving a stale stage behind.
    private static readonly TimeSpan ProbingStageLifetime = TimeSpan.FromSeconds(15);

    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly Lock sync = new();
    private ConnectionProgress? current;

    public event Action? Changed;

    public ConnectionProgress? Current
    {
        get
        {
            lock (sync)
            {
                if (current is { Stage: ConnectionStage.ProbingTunnel } probing &&
                    clock.GetUtcNow() - probing.StageStartedAt > ProbingStageLifetime)
                {
                    current = null;
                }

                return current;
            }
        }
    }

    /// <summary>Starts a new operation at <paramref name="stage"/>.</summary>
    public void Begin(ConnectionStage stage, int attempt = 1)
    {
        var now = clock.GetUtcNow();
        Set(new ConnectionProgress(stage, now, now, Math.Max(1, attempt)));
    }

    /// <summary>Moves the running operation to <paramref name="stage"/>, keeping its start time.</summary>
    public void Advance(ConnectionStage stage)
    {
        var now = clock.GetUtcNow();
        lock (sync)
        {
            if (current?.Stage == stage)
            {
                return;
            }

            current = current is null
                ? new ConnectionProgress(stage, now, now)
                : current with { Stage = stage, StageStartedAt = now };
        }

        Changed?.Invoke();
    }

    /// <summary>Shows a pending or running transport recovery, keeping the episode's start time.</summary>
    public void Recovering(int attempt)
    {
        var now = clock.GetUtcNow();
        lock (sync)
        {
            if (current is { Stage: ConnectionStage.RecoveringTransport } recovering && recovering.Attempt == attempt)
            {
                return;
            }

            current = current is { Stage: ConnectionStage.RecoveringTransport } episode
                ? episode with { StageStartedAt = now, Attempt = Math.Max(1, attempt) }
                : new ConnectionProgress(ConnectionStage.RecoveringTransport, now, now, Math.Max(1, attempt));
        }

        Changed?.Invoke();
    }

    public void Clear() => Set(null);

    /// <summary>Clears the progress only while it is still at <paramref name="stage"/>.</summary>
    public void ClearIf(ConnectionStage stage)
    {
        lock (sync)
        {
            if (current?.Stage != stage)
            {
                return;
            }

            current = null;
        }

        Changed?.Invoke();
    }

    private void Set(ConnectionProgress? value)
    {
        lock (sync)
        {
            if (current == value)
            {
                return;
            }

            current = value;
        }

        Changed?.Invoke();
    }
}
