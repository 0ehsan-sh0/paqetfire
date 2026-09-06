using PaqetFire.Core.Engines;

namespace PaqetFire.Core.Connections;

public sealed class ConnectionController : IConnectionController, IDisposable
{
    private readonly IEngineAdapter _paqet;
    private readonly IEngineAdapter _xray;
    private readonly IEngineAdapter _proxiFyre;
    private readonly SemaphoreSlim _transitionGate = new(1, 1);
    private ConnectionState? _transitionState;
    private bool _disposed;

    public ConnectionController(
        IEngineAdapter paqet,
        IEngineAdapter xray,
        IEngineAdapter proxiFyre)
    {
        ArgumentNullException.ThrowIfNull(paqet);
        ArgumentNullException.ThrowIfNull(xray);
        ArgumentNullException.ThrowIfNull(proxiFyre);

        if (paqet.Kind != EngineKind.Paqet)
        {
            throw new ArgumentException("The Paqet adapter must identify itself as EngineKind.Paqet.", nameof(paqet));
        }

        if (xray.Kind != EngineKind.Xray)
        {
            throw new ArgumentException("The Xray adapter must identify itself as EngineKind.Xray.", nameof(xray));
        }

        if (proxiFyre.Kind != EngineKind.ProxiFyre)
        {
            throw new ArgumentException(
                "The ProxiFyre adapter must identify itself as EngineKind.ProxiFyre.",
                nameof(proxiFyre));
        }

        _paqet = paqet;
        _xray = xray;
        _proxiFyre = proxiFyre;
    }

    public async ValueTask<ConnectionStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var transitionState = _transitionState;
        var paqetStatus = await _paqet.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        var xrayStatus = await _xray.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        var proxiFyreStatus = await _proxiFyre.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        var derivedState = DeriveState(paqetStatus.State, xrayStatus.State, proxiFyreStatus.State);

        if (transitionState is not null && transitionState == _transitionState)
        {
            derivedState = transitionState.Value;
        }

        return new ConnectionStatus(
            derivedState,
            paqetStatus,
            xrayStatus,
            proxiFyreStatus,
            CreateStatusDetail(derivedState, paqetStatus.State, xrayStatus.State, proxiFyreStatus.State),
            DateTimeOffset.UtcNow);
    }

    public async ValueTask ConnectAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _transitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            _transitionState = ConnectionState.Connecting;

            var paqetInitiallyRunning = await IsRunningAsync(_paqet, cancellationToken).ConfigureAwait(false);
            var xrayInitiallyRunning = await IsRunningAsync(_xray, cancellationToken).ConfigureAwait(false);
            var proxiFyreInitiallyRunning = await IsRunningAsync(_proxiFyre, cancellationToken).ConfigureAwait(false);

            if (paqetInitiallyRunning && xrayInitiallyRunning && proxiFyreInitiallyRunning)
            {
                return;
            }

            var paqetStartAttempted = false;
            var xrayStartAttempted = false;
            var proxiFyreStartAttempted = false;
            var activeEngine = EngineKind.Paqet;

            try
            {
                if (!paqetInitiallyRunning)
                {
                    paqetStartAttempted = true;
                    await StartAndVerifyAsync(_paqet, cancellationToken).ConfigureAwait(false);
                }

                activeEngine = EngineKind.Xray;

                if (!xrayInitiallyRunning)
                {
                    xrayStartAttempted = true;
                    await StartAndVerifyAsync(_xray, cancellationToken).ConfigureAwait(false);
                }

                activeEngine = EngineKind.ProxiFyre;

                if (!proxiFyreInitiallyRunning)
                {
                    proxiFyreStartAttempted = true;
                    await StartAndVerifyAsync(_proxiFyre, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception error)
            {
                var rollbackErrors = await RollBackConnectAsync(
                        paqetStartAttempted,
                        xrayStartAttempted,
                        proxiFyreStartAttempted)
                    .ConfigureAwait(false);

                if (error is OperationCanceledException && rollbackErrors.Count == 0)
                {
                    throw;
                }

                throw new ConnectionTransitionException("connect", activeEngine, error, rollbackErrors);
            }
        }
        finally
        {
            _transitionState = null;
            _transitionGate.Release();
        }
    }

    public async ValueTask DisconnectAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _transitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            _transitionState = ConnectionState.Disconnecting;

            try
            {
                await StopAndVerifyAsync(_proxiFyre, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception error)
            {
                throw new ConnectionTransitionException("disconnect", EngineKind.ProxiFyre, error);
            }

            try
            {
                await StopAndVerifyAsync(_xray, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception error)
            {
                throw new ConnectionTransitionException("disconnect", EngineKind.Xray, error);
            }

            try
            {
                await StopAndVerifyAsync(_paqet, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception error)
            {
                throw new ConnectionTransitionException("disconnect", EngineKind.Paqet, error);
            }
        }
        finally
        {
            _transitionState = null;
            _transitionGate.Release();
        }
    }

    public async ValueTask GuardAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _transitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            _transitionState = ConnectionState.Disconnecting;

            try
            {
                await StartAndVerifyAsync(_proxiFyre, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception error)
            {
                throw new ConnectionTransitionException("arm routing kill switch", EngineKind.ProxiFyre, error);
            }

            try
            {
                await StopAndVerifyAsync(_xray, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception error)
            {
                throw new ConnectionTransitionException("arm routing kill switch", EngineKind.Xray, error);
            }

            try
            {
                await StopAndVerifyAsync(_paqet, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception error)
            {
                throw new ConnectionTransitionException("arm routing kill switch", EngineKind.Paqet, error);
            }
        }
        finally
        {
            _transitionState = null;
            _transitionGate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _transitionGate.Dispose();
    }

    private static async ValueTask<bool> IsRunningAsync(
        IEngineAdapter adapter,
        CancellationToken cancellationToken)
    {
        var status = await adapter.GetStatusAsync(cancellationToken).ConfigureAwait(false);

        if (status.State == EngineState.NotInstalled)
        {
            throw new InvalidOperationException($"The {adapter.Kind} engine is not installed.");
        }

        return status.State == EngineState.Running;
    }

    private static async ValueTask StartAndVerifyAsync(
        IEngineAdapter adapter,
        CancellationToken cancellationToken)
    {
        await adapter.StartAsync(cancellationToken).ConfigureAwait(false);
        var status = await adapter.GetStatusAsync(cancellationToken).ConfigureAwait(false);

        if (status.State != EngineState.Running)
        {
            throw new InvalidOperationException(
                $"The {adapter.Kind} engine reported {status.State} after startup instead of Running.");
        }
    }

    private static async ValueTask StopAndVerifyAsync(
        IEngineAdapter adapter,
        CancellationToken cancellationToken)
    {
        var status = await adapter.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        if (status.State is EngineState.Stopped or EngineState.NotInstalled)
        {
            return;
        }

        await adapter.StopAsync(cancellationToken).ConfigureAwait(false);
        status = await adapter.GetStatusAsync(cancellationToken).ConfigureAwait(false);

        if (status.State is not (EngineState.Stopped or EngineState.NotInstalled))
        {
            throw new InvalidOperationException(
                $"The {adapter.Kind} engine reported {status.State} after shutdown instead of Stopped.");
        }
    }

    private async ValueTask<IReadOnlyList<Exception>> RollBackConnectAsync(
        bool paqetStartAttempted,
        bool xrayStartAttempted,
        bool proxiFyreStartAttempted)
    {
        List<Exception>? errors = null;

        if (proxiFyreStartAttempted)
        {
            await TryRollbackStopAsync(_proxiFyre).ConfigureAwait(false);
        }

        if (xrayStartAttempted)
        {
            await TryRollbackStopAsync(_xray).ConfigureAwait(false);
        }

        if (paqetStartAttempted)
        {
            await TryRollbackStopAsync(_paqet).ConfigureAwait(false);
        }

        return errors ?? [];

        async ValueTask TryRollbackStopAsync(IEngineAdapter adapter)
        {
            try
            {
                await StopAndVerifyAsync(adapter, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                errors ??= [];
                errors.Add(new InvalidOperationException(
                    $"Failed to stop {adapter.Kind} while rolling back the connection.",
                    error));
            }
        }
    }

    private static ConnectionState DeriveState(
        EngineState paqet,
        EngineState xray,
        EngineState proxiFyre)
    {
        if (paqet == EngineState.NotInstalled || xray == EngineState.NotInstalled || proxiFyre == EngineState.NotInstalled)
        {
            return ConnectionState.NotReady;
        }

        if (paqet == EngineState.Running && xray == EngineState.Running && proxiFyre == EngineState.Running)
        {
            return ConnectionState.Connected;
        }

        if (paqet == EngineState.Stopped && xray == EngineState.Stopped && proxiFyre == EngineState.Stopped)
        {
            return ConnectionState.Disconnected;
        }

        if (paqet == EngineState.Stopped && xray == EngineState.Stopped && proxiFyre == EngineState.Running)
        {
            return ConnectionState.Guarded;
        }

        if (paqet == EngineState.Faulted || xray == EngineState.Faulted || proxiFyre == EngineState.Faulted)
        {
            return ConnectionState.Faulted;
        }

        if (paqet == EngineState.Starting || xray == EngineState.Starting || proxiFyre == EngineState.Starting)
        {
            return ConnectionState.Connecting;
        }

        if (paqet == EngineState.Stopping || xray == EngineState.Stopping || proxiFyre == EngineState.Stopping)
        {
            return ConnectionState.Disconnecting;
        }

        return ConnectionState.Degraded;
    }

    private static string? CreateStatusDetail(
        ConnectionState connection,
        EngineState paqet,
        EngineState xray,
        EngineState proxiFyre) => connection switch
        {
            ConnectionState.NotReady => "One or more bundled engines are not installed.",
            ConnectionState.Guarded => "The routing kill switch is active; routed applications cannot connect directly.",
            ConnectionState.Degraded => $"Engine states do not form a safe connection (Paqet: {paqet}, Xray: {xray}, ProxiFyre: {proxiFyre}).",
            ConnectionState.Faulted => $"An engine is faulted (Paqet: {paqet}, Xray: {xray}, ProxiFyre: {proxiFyre}).",
            _ => null,
        };
}
