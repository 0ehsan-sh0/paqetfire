using PaqetFire.Core.Connections;
using Xunit;

namespace PaqetFire.Core.Tests;

public sealed class TunnelHealthPolicyTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TransientFailuresDoNotRestartTheTransport()
    {
        var policy = new TunnelHealthPolicy();

        Assert.Equal(TunnelHealthAction.None, policy.Observe(false, Start));
        Assert.Equal(TunnelHealthAction.None, policy.Observe(false, Start.AddSeconds(10)));
        Assert.Equal(TunnelHealthAction.None, policy.Observe(true, Start.AddSeconds(20)));

        Assert.Equal(TunnelHealthState.Healthy, policy.Report.State);
        Assert.Equal(0, policy.Report.ConsecutiveFailures);
        Assert.Equal(Start.AddSeconds(20), policy.Report.LastSuccessAt);
    }

    [Fact]
    public void RepeatedFailuresRestartWithDoublingBackoff()
    {
        var policy = new TunnelHealthPolicy(new TunnelHealthOptions
        {
            InitialRecoveryBackoff = TimeSpan.FromSeconds(30),
            MaximumRecoveryBackoff = TimeSpan.FromSeconds(100),
        });
        policy.Observe(false, Start);
        policy.Observe(false, Start.AddSeconds(10));

        Assert.Equal(TunnelHealthAction.RestartTransport, policy.Observe(false, Start.AddSeconds(20)));
        Assert.Equal(TunnelHealthState.Recovering, policy.Report.State);
        policy.RecoveryCompleted();
        Assert.Equal(TunnelHealthState.Stalled, policy.Report.State);

        Assert.Equal(TunnelHealthAction.None, policy.Observe(false, Start.AddSeconds(40)));
        Assert.Equal(TunnelHealthAction.RestartTransport, policy.Observe(false, Start.AddSeconds(50)));
        Assert.Equal(Start.AddSeconds(110), policy.Report.NextRecoveryAt);
        policy.RecoveryCompleted();

        Assert.Equal(TunnelHealthAction.RestartTransport, policy.Observe(false, Start.AddSeconds(110)));
        Assert.Equal(Start.AddSeconds(210), policy.Report.NextRecoveryAt);
        Assert.Equal(3, policy.Report.RecoveryAttempts);
    }

    [Fact]
    public void ASuccessfulProbeClearsTheBackoff()
    {
        var policy = new TunnelHealthPolicy();
        for (var index = 0; index < 3; index++) policy.Observe(false, Start.AddSeconds(index * 10));
        policy.RecoveryCompleted();

        policy.Observe(true, Start.AddSeconds(40));

        Assert.Equal(new TunnelHealthReport(TunnelHealthState.Healthy, Start.AddSeconds(40), 0, 0, null), policy.Report);
        policy.Reset();
        Assert.Equal(TunnelHealthReport.Unknown, policy.Report);
    }
}
