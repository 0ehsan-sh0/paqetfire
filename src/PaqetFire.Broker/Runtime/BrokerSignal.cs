namespace PaqetFire.Broker.Runtime;

/// <summary>
/// Wakes the broker monitor before its next tick, for example when an engine exits or a
/// connection stage changes. Notifications coalesce: one wake-up covers all of them.
/// </summary>
public sealed class BrokerSignal
{
    private readonly SemaphoreSlim pending = new(0, 1);

    public void Notify()
    {
        try
        {
            pending.Release();
        }
        catch (SemaphoreFullException)
        {
            // A wake-up is already pending.
        }
    }

    /// <returns>True when woken by <see cref="Notify"/>, false when the timeout elapsed.</returns>
    public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
        pending.WaitAsync(timeout < TimeSpan.Zero ? TimeSpan.Zero : timeout, cancellationToken);
}
