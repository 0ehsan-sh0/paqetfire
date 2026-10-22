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
    public void AFailedProbeIsRepeatedQuicklySoAStallIsActedOnWithinSeconds()
    {
        var policy = new TunnelHealthPolicy();
        Assert.Equal(TimeSpan.FromSeconds(10), policy.NextProbeDelay);

        policy.Observe(false, Start);
        Assert.Equal(TimeSpan.FromSeconds(3), policy.NextProbeDelay);
        policy.Observe(false, Start.AddSeconds(3));

        Assert.Equal(TunnelHealthAction.RestartTransport, policy.Observe(false, Start.AddSeconds(6)));
        policy.Observe(true, Start.AddSeconds(9));
        Assert.Equal(TimeSpan.FromSeconds(10), policy.NextProbeDelay);
    }

    [Fact]
    public void BackoffIsCappedAtAMinuteWhileOnlineAndResetByANewNetwork()
    {
        var policy = new TunnelHealthPolicy();
        var now = Start;
        for (var restart = 0; restart < 6; restart++)
        {
            while (policy.Observe(false, now) != TunnelHealthAction.RestartTransport)
            {
                now = now.AddSeconds(3);
            }

            policy.RecoveryCompleted();
        }

        Assert.Equal(now.AddSeconds(60), policy.Report.NextRecoveryAt);
        Assert.Equal(TunnelHealthAction.None, policy.Observe(false, now.AddSeconds(3)));

        policy.ResetBackoff();
        Assert.Equal(TunnelHealthAction.RestartTransport, policy.Observe(false, now.AddSeconds(6)));
        Assert.Equal(1, policy.Report.RecoveryAttempts);
    }

    [Fact]
    public void BackoffMayGrowLongerWithoutANetwork()
    {
        var policy = new TunnelHealthPolicy(new TunnelHealthOptions
        {
            InitialRecoveryBackoff = TimeSpan.FromSeconds(100),
            FailuresBeforeStalled = 1,
        });

        policy.Observe(false, Start, networkAvailable: false);

        Assert.Equal(Start.AddSeconds(100), policy.Report.NextRecoveryAt);
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
