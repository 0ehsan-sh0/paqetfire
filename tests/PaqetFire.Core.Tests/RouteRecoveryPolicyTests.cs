using PaqetFire.Core.Connections;
using PaqetFire.Core.Engines;
using Xunit;

namespace PaqetFire.Core.Tests;

public sealed class RouteRecoveryPolicyTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void CarrierFaultRestartsPaqetWithShortBackoffThenReleasesTheRoute()
    {
        var policy = new RouteRecoveryPolicy();
        var now = Start;
        var expectedDelays = new[] { 1, 2, 5, 10 };

        for (var attempt = 1; attempt <= 5; attempt++)
        {
            var decision = Evaluate(policy, now, desired: true, ConnectionState.Faulted, CarrierDown());
            Assert.Equal(RouteRecoveryAction.RestartTransport, decision.Action);
            Assert.Equal(attempt, decision.Attempt);
            policy.RecordAttempt(now);

            if (attempt < 5)
            {
                var waiting = Evaluate(policy, now.AddMilliseconds(100), desired: true, ConnectionState.Faulted, CarrierDown());
                Assert.True(waiting.IsWaiting);
                Assert.Equal(attempt + 1, waiting.Attempt);
                Assert.Equal(now.AddSeconds(expectedDelays[attempt - 1]), waiting.RetryAt);
                now = waiting.RetryAt!.Value;
            }
        }

        var release = Evaluate(policy, now.AddMinutes(1), desired: true, ConnectionState.Faulted, CarrierDown());
        Assert.Equal(RouteRecoveryAction.SafeDisconnect, release.Action);
        Assert.True(policy.IsSuspended);

        // Suspended: the released route is not reconnected until something re-arms recovery.
        Assert.Equal(RouteRecoveryAction.None,
            Evaluate(policy, now.AddMinutes(2), desired: true, ConnectionState.Guarded, Guarded(), killSwitch: true).Action);
        policy.Reset();
        Assert.Equal(RouteRecoveryAction.Reconnect,
            Evaluate(policy, now.AddMinutes(3), desired: true, ConnectionState.Guarded, Guarded(), killSwitch: true).Action);
    }

    [Fact]
    public void BackoffIsCappedAtTheLastDelay()
    {
        var policy = new RouteRecoveryPolicy(new RouteRecoveryOptions { MaximumConsecutiveFailures = 10 });
        for (var attempt = 0; attempt < 7; attempt++) policy.RecordAttempt(Start);

        Assert.Equal(Start.AddSeconds(30), policy.NextAttemptAt);
    }

    [Fact]
    public void RouteTheUserWantsIsReconnectedButOtherwiseLeftAlone()
    {
        var wanted = new RouteRecoveryPolicy();
        var unwanted = new RouteRecoveryPolicy();

        Assert.Equal(RouteRecoveryAction.Reconnect,
            Evaluate(wanted, Start, desired: true, ConnectionState.Disconnected, Stopped()).Action);
        Assert.Equal(RouteRecoveryAction.None,
            Evaluate(unwanted, Start, desired: false, ConnectionState.Disconnected, Stopped()).Action);
        Assert.Equal(RouteRecoveryAction.None,
            Evaluate(wanted, Start, desired: true, ConnectionState.Connected, Running()).Action);
    }

    [Fact]
    public void BrokenPolicyEngineIsReleasedFirstAndThenReconnected()
    {
        var policy = new RouteRecoveryPolicy();
        EngineStatus[] xrayDown =
        [
            Status(EngineKind.Paqet, EngineState.Running),
            Status(EngineKind.Xray, EngineState.Faulted),
            Status(EngineKind.ProxiFyre, EngineState.Running),
        ];

        Assert.Equal(RouteRecoveryAction.SafeDisconnect,
            Evaluate(policy, Start, desired: true, ConnectionState.Faulted, xrayDown).Action);
        Assert.Equal(0, policy.Attempts);
        Assert.Equal(RouteRecoveryAction.Reconnect,
            Evaluate(policy, Start, desired: true, ConnectionState.Disconnected, Stopped()).Action);
    }

    [Fact]
    public void UnwantedCarrierFaultRestoresTheSafeState()
    {
        var decision = Evaluate(new RouteRecoveryPolicy(), Start, desired: false, ConnectionState.Faulted, CarrierDown());

        Assert.Equal(RouteRecoveryAction.SafeDisconnect, decision.Action);
    }

    [Theory]
    [InlineData(ConnectionState.Connecting)]
    [InlineData(ConnectionState.Disconnecting)]
    [InlineData(ConnectionState.NotReady)]
    public void TransitionsAreNeverInterrupted(ConnectionState state)
    {
        Assert.Equal(RouteRecoveryAction.None,
            Evaluate(new RouteRecoveryPolicy(), Start, desired: true, state, CarrierDown()).Action);
    }

    [Fact]
    public void ACarrierThatStartsAndCrashesStillReachesTheLimit()
    {
        var policy = new RouteRecoveryPolicy(new RouteRecoveryOptions { MaximumConsecutiveFailures = 2 });
        policy.RecordAttempt(Start);
        // The restart "succeeded" (Connected) but no healthy probe reset the episode.
        Assert.Equal(RouteRecoveryAction.None,
            Evaluate(policy, Start.AddSeconds(5), desired: true, ConnectionState.Connected, Running()).Action);
        policy.RecordAttempt(Start.AddSeconds(5));

        Assert.Equal(RouteRecoveryAction.SafeDisconnect,
            Evaluate(policy, Start.AddMinutes(1), desired: true, ConnectionState.Faulted, CarrierDown()).Action);
    }

    private static RouteRecoveryDecision Evaluate(
        RouteRecoveryPolicy policy,
        DateTimeOffset now,
        bool desired,
        ConnectionState state,
        EngineStatus[] engines,
        bool killSwitch = false) =>
        policy.Evaluate(now, desired, state, killSwitch, engines);

    private static EngineStatus[] CarrierDown() =>
    [
        Status(EngineKind.Paqet, EngineState.Faulted),
        Status(EngineKind.Xray, EngineState.Running),
        Status(EngineKind.ProxiFyre, EngineState.Running),
    ];

    private static EngineStatus[] Running() =>
    [
        Status(EngineKind.Paqet, EngineState.Running),
        Status(EngineKind.Xray, EngineState.Running),
        Status(EngineKind.ProxiFyre, EngineState.Running),
    ];

    private static EngineStatus[] Guarded() =>
    [
        Status(EngineKind.Paqet, EngineState.Stopped),
        Status(EngineKind.Xray, EngineState.Stopped),
        Status(EngineKind.ProxiFyre, EngineState.Running),
    ];

    private static EngineStatus[] Stopped() =>
    [
        Status(EngineKind.Paqet, EngineState.Stopped),
        Status(EngineKind.Xray, EngineState.Stopped),
        Status(EngineKind.ProxiFyre, EngineState.Stopped),
    ];

    private static EngineStatus Status(EngineKind kind, EngineState state) => new(kind, state, "test");
}
