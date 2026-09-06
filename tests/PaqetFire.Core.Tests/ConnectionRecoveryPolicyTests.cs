using PaqetFire.Core.Connections;
using PaqetFire.Core.Engines;
using Xunit;

namespace PaqetFire.Core.Tests;

public sealed class ConnectionRecoveryPolicyTests
{
    [Fact]
    public void ConnectingChain_DoesNotRequestRecoveryWhilePaqetIsAlreadyRunning()
    {
        EngineStatus[] engines =
        [
            Status(EngineKind.Paqet, EngineState.Running),
            Status(EngineKind.Xray, EngineState.Starting),
            Status(EngineKind.ProxiFyre, EngineState.Stopped),
        ];

        var recover = ConnectionRecoveryPolicy.RequiresSafeRecovery(
            ConnectionState.Connecting,
            isKillSwitchEnabled: false,
            engines);

        Assert.False(recover);
    }

    [Theory]
    [InlineData(ConnectionState.Connected, false, false)]
    [InlineData(ConnectionState.Disconnected, false, false)]
    [InlineData(ConnectionState.Disconnecting, false, false)]
    [InlineData(ConnectionState.Guarded, true, false)]
    [InlineData(ConnectionState.Guarded, false, true)]
    [InlineData(ConnectionState.Degraded, false, true)]
    [InlineData(ConnectionState.Faulted, false, true)]
    public void StableAndBrokenStates_HaveExpectedRecoveryPolicy(
        ConnectionState state,
        bool killSwitchEnabled,
        bool expected)
    {
        EngineStatus[] engines = state switch
        {
            ConnectionState.Connected =>
            [
                Status(EngineKind.Paqet, EngineState.Running),
                Status(EngineKind.Xray, EngineState.Running),
                Status(EngineKind.ProxiFyre, EngineState.Running),
            ],
            ConnectionState.Disconnected or ConnectionState.Disconnecting =>
            [
                Status(EngineKind.Paqet, EngineState.Stopped),
                Status(EngineKind.Xray, EngineState.Stopped),
                Status(EngineKind.ProxiFyre, EngineState.Stopped),
            ],
            ConnectionState.Guarded =>
            [
                Status(EngineKind.Paqet, EngineState.Stopped),
                Status(EngineKind.Xray, EngineState.Stopped),
                Status(EngineKind.ProxiFyre, EngineState.Running),
            ],
            ConnectionState.Faulted =>
            [
                Status(EngineKind.Paqet, EngineState.Running),
                Status(EngineKind.Xray, EngineState.Faulted),
                Status(EngineKind.ProxiFyre, EngineState.Stopped),
            ],
            _ =>
            [
                Status(EngineKind.Paqet, EngineState.Running),
                Status(EngineKind.Xray, EngineState.Stopped),
                Status(EngineKind.ProxiFyre, EngineState.Stopped),
            ],
        };

        Assert.Equal(
            expected,
            ConnectionRecoveryPolicy.RequiresSafeRecovery(state, killSwitchEnabled, engines));
    }

    private static EngineStatus Status(EngineKind kind, EngineState state) =>
        new(kind, state, "test");
}
