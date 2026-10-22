using System.Net.NetworkInformation;
using PaqetFire.Broker.Runtime;

namespace PaqetFire.Broker.Network;

/// <summary>
/// Turns Windows address-change and resume notifications into one debounced call to the
/// runtime. A Wi-Fi roam or DHCP renewal raises several events within a second; acting
/// on each would restart Paqet repeatedly against a network that is still settling.
/// </summary>
public sealed class NetworkChangeMonitor(IPaqetFireRuntime runtime, ILogger<NetworkChangeMonitor> logger)
{
    public static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(1500);

    private readonly SemaphoreSlim changed = new(0, 1);
    private int resumePending;

    /// <summary>Called by the service host when Windows resumes from sleep or hibernation.</summary>
    public void NotifyResumed()
    {
        Interlocked.Exchange(ref resumePending, 1);
        Signal();
    }

    public void NotifyChanged() => Signal();

    public async Task RunAsync(CancellationToken stoppingToken)
    {
        NetworkChange.NetworkAddressChanged += OnAddressChanged;
        NetworkChange.NetworkAvailabilityChanged += OnAvailabilityChanged;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await changed.WaitAsync(stoppingToken).ConfigureAwait(false);
                // Restart the quiet period on every further event.
                while (await changed.WaitAsync(Debounce, stoppingToken).ConfigureAwait(false))
                {
                }

                var resumed = Interlocked.Exchange(ref resumePending, 0) == 1;
                try
                {
                    await runtime.HandleNetworkChangeAsync(resumed, stoppingToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
                {
                    // One failed or cancelled adjustment must not end network handling for the
                    // rest of the service's life; the next change event tries again.
                    logger.LogWarning(exception, "The route could not be adapted to the network change.");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal service shutdown.
        }
        finally
        {
            NetworkChange.NetworkAddressChanged -= OnAddressChanged;
            NetworkChange.NetworkAvailabilityChanged -= OnAvailabilityChanged;
        }
    }

    private void OnAddressChanged(object? sender, EventArgs eventArgs) => Signal();

    private void OnAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs eventArgs) => Signal();

    private void Signal()
    {
        try
        {
            changed.Release();
        }
        catch (SemaphoreFullException)
        {
            // A change is already pending.
        }
    }
}
