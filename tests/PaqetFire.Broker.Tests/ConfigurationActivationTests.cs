using PaqetFire.Broker.Runtime;
using PaqetFire.Core.Connections;
using Xunit;

namespace PaqetFire.Broker.Tests;

public sealed class ConfigurationActivationTests
{
    [Fact]
    public async Task FailedConnectReturnsWarningAfterRestoringGuard()
    {
        var failure = new IOException("connect failed");
        var controller = new RecordingController { ConnectError = failure };
        var result = await ConfigurationActivation.ApplyAsync(controller, () => Task.CompletedTask,
            true, true, true, CancellationToken.None);
        Assert.Same(failure, result);
        Assert.Equal(["disconnect", "guard", "connect", "guard"], controller.Calls);
    }

    [Fact]
    public async Task FailedPersistenceLeavesExistingGuardUntouched()
    {
        var controller = new RecordingController();
        await Assert.ThrowsAsync<IOException>(() => ConfigurationActivation.ApplyAsync(controller,
            () => throw new IOException("disk full"), true, true, true, CancellationToken.None));
        Assert.Empty(controller.Calls);
    }

    [Fact]
    public async Task NewPolicyRestartsOnlyAfterPersistence()
    {
        var controller = new RecordingController();
        var result = await ConfigurationActivation.ApplyAsync(controller,
            () => { controller.Calls.Add("persist"); return Task.CompletedTask; }, true, true, true, CancellationToken.None);
        Assert.Null(result);
        Assert.Equal(["persist", "disconnect", "guard", "connect"], controller.Calls);
    }

    [Fact]
    public async Task CancelledActivationRestoresGuardWithoutCancelledToken()
    {
        var controller = new RecordingController { DisconnectError = new OperationCanceledException() };
        await Assert.ThrowsAsync<OperationCanceledException>(() => ConfigurationActivation.ApplyAsync(controller,
            () => Task.CompletedTask, true, true, true, new CancellationToken(true)));
        Assert.Equal(["disconnect", "guard"], controller.Calls);
        Assert.False(controller.LastGuardToken.IsCancellationRequested);
    }

    [Fact]
    public async Task FailedRestorationIsSurfacedAlongsideActivationFailure()
    {
        var controller = new RecordingController { DisconnectError = new IOException("stop failed"), GuardError = new IOException("guard failed") };
        var error = await Assert.ThrowsAsync<AggregateException>(() => ConfigurationActivation.ApplyAsync(controller,
            () => Task.CompletedTask, true, true, false, CancellationToken.None));
        Assert.Equal(2, error.InnerExceptions.Count);
    }

    [Fact]
    public async Task DisablingGuardIntentionallyDoesNotRestartIt()
    {
        var controller = new RecordingController();
        Assert.Null(await ConfigurationActivation.ApplyAsync(controller, () => Task.CompletedTask,
            true, false, false, CancellationToken.None));
        Assert.Equal(["disconnect"], controller.Calls);
    }

    private sealed class RecordingController : IConnectionController
    {
        public List<string> Calls { get; } = [];
        public Exception? DisconnectError { get; init; }
        public Exception? GuardError { get; init; }
        public Exception? ConnectError { get; init; }
        public CancellationToken LastGuardToken { get; private set; }
        public ValueTask<ConnectionStatus> GetStatusAsync(CancellationToken token) => throw new NotSupportedException();
        public ValueTask DisconnectAsync(CancellationToken token)
        {
            Calls.Add("disconnect");
            return DisconnectError is null ? ValueTask.CompletedTask : ValueTask.FromException(DisconnectError);
        }
        public ValueTask GuardAsync(CancellationToken token)
        {
            Calls.Add("guard");
            LastGuardToken = token;
            return GuardError is null ? ValueTask.CompletedTask : ValueTask.FromException(GuardError);
        }
        public ValueTask ConnectAsync(CancellationToken token)
        {
            Calls.Add("connect");
            return ConnectError is null ? ValueTask.CompletedTask : ValueTask.FromException(ConnectError);
        }
    }
}
