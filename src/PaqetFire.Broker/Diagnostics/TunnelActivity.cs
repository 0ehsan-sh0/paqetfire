namespace PaqetFire.Broker.Diagnostics;

/// <summary>
/// Evidence that the Paqet tunnel carried traffic recently, collected by the live
/// statistics poll. The watchdog accepts it instead of sending its own probe, which
/// avoids duplicate probe traffic and false stalls while a busy tunnel delays probes.
/// </summary>
public sealed class TunnelActivity
{
    private long lastEvidenceTicks;
    private long lastDownlinkBytes = -1;

    public DateTimeOffset? LastEvidenceAt => Interlocked.Read(ref lastEvidenceTicks) is var ticks and > 0
        ? new DateTimeOffset(ticks, TimeSpan.Zero)
        : null;

    /// <summary>A latency probe through Paqet completed.</summary>
    public void RecordLatency(DateTimeOffset now) => Record(now);

    /// <summary>
    /// Records Xray's cumulative Paqet downlink counter. Growth means bytes arrived from
    /// the server; a lower value means Xray restarted and only resets the baseline.
    /// </summary>
    public void RecordDownlink(long downlinkBytes, DateTimeOffset now)
    {
        var previous = Interlocked.Exchange(ref lastDownlinkBytes, downlinkBytes);
        if (previous >= 0 && downlinkBytes > previous)
        {
            Record(now);
        }
    }

    public bool HasEvidenceSince(DateTimeOffset since) => LastEvidenceAt is { } last && last >= since;

    public void Reset()
    {
        Interlocked.Exchange(ref lastEvidenceTicks, 0);
        Interlocked.Exchange(ref lastDownlinkBytes, -1);
    }

    private void Record(DateTimeOffset now)
    {
        var ticks = now.UtcTicks;
        long previous;
        do
        {
            previous = Interlocked.Read(ref lastEvidenceTicks);
            if (previous >= ticks)
            {
                return;
            }
        }
        while (Interlocked.CompareExchange(ref lastEvidenceTicks, ticks, previous) != previous);
    }
}
