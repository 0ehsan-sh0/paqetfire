using System.ServiceProcess;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Options;

namespace PaqetFire.Broker.Network;

/// <summary>
/// The Windows service lifetime with power events enabled. After sleep, Paqet's raw
/// sockets and KCP sessions are stale even when the network looks unchanged, so the
/// broker must learn about resume itself instead of waiting for the desktop app.
/// </summary>
public sealed class PowerAwareServiceLifetime : WindowsServiceLifetime
{
    private readonly NetworkChangeMonitor networkMonitor;

    public PowerAwareServiceLifetime(
        IHostEnvironment environment,
        IHostApplicationLifetime applicationLifetime,
        ILoggerFactory loggerFactory,
        IOptions<HostOptions> optionsAccessor,
        IOptions<WindowsServiceLifetimeOptions> windowsServiceOptionsAccessor,
        NetworkChangeMonitor networkMonitor)
        : base(environment, applicationLifetime, loggerFactory, optionsAccessor, windowsServiceOptionsAccessor)
    {
        this.networkMonitor = networkMonitor;
        CanHandlePowerEvent = true;
    }

    protected override bool OnPowerEvent(PowerBroadcastStatus powerStatus)
    {
        // ResumeAutomatic is sent on every resume; ResumeSuspend follows only after user
        // input, possibly minutes later, and must not restart a working tunnel again.
        if (powerStatus == PowerBroadcastStatus.ResumeAutomatic)
        {
            networkMonitor.NotifyResumed();
        }

        return base.OnPowerEvent(powerStatus);
    }
}
