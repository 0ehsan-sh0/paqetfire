using System.Runtime.ExceptionServices;
using PaqetFire.Core.Engines;
using PaqetFire.Core.Ipc;

namespace PaqetFire.Core.Connections;

public sealed class ConnectionController : IConnectionController, IDisposable
{
    private readonly IEngineAdapter _paqet;
    private readonly IEngineAdapter _xray;
    private readonly IEngineAdapter _proxiFyre;
    private readonly IEngineAdapter? _openVpn;
    private readonly SemaphoreSlim _transitionGate = new(1, 1);
    private ConnectionState? _transitionState;
    private bool _disposed;

    public ConnectionController(
        IEngineAdapter paqet,
        IEngineAdapter xray,
        IEngineAdapter proxiFyre,
        IEngineAdapter? openVpn = null)
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

        if (openVpn is not null && openVpn.Kind != EngineKind.OpenVpn)
        {
            throw new ArgumentException(
                "The OpenVPN adapter must identify itself as EngineKind.OpenVpn.",
                nameof(openVpn));
        }

        _paqet = paqet;
        _xray = xray;
        _proxiFyre = proxiFyre;
        _openVpn = openVpn;
    }

    // Deliberately does not take the transition gate so the desktop can observe a long
    // transition; adapters must therefore tolerate GetStatusAsync during Start/Stop.
    public async ValueTask<ConnectionStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var transitionState = _transitionState;
        var paqetStatus = await _paqet.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        var xrayStatus = await _xray.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        var proxiFyreStatus = await _proxiFyre.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        var openVpnStatus = _openVpn is not null
            ? await _openVpn.GetStatusAsync(cancellationToken).ConfigureAwait(false)
            : null;
        var derivedState = DeriveState(paqetStatus.State, xrayStatus.State, proxiFyreStatus.State, openVpnStatus?.State);

        if (transitionState is not null && transitionState == _transitionState)
        {
            derivedState = transitionState.Value;
        }

        return new ConnectionStatus(
            derivedState,
            paqetStatus,
            xrayStatus,
            proxiFyreStatus,
            CreateStatusDetail(derivedState, paqetStatus.State, xrayStatus.State, proxiFyreStatus.State, openVpnStatus?.State),
            DateTimeOffset.UtcNow,
            openVpnStatus);
    }

    public ValueTask ConnectAsync(CancellationToken cancellationToken) => ConnectAsync(null, cancellationToken);

    public async ValueTask ConnectAsync(Action<ConnectionStage>? stageStarted, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _transitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            _transitionState = ConnectionState.Connecting;

            var paqetInitiallyRunning = await IsRunningAsync(_paqet, cancellationToken).ConfigureAwait(false);
            var xrayInitiallyRunning = await IsRunningAsync(_xray, cancellationToken).ConfigureAwait(false);
            var proxiFyreInitiallyRunning = await IsRunningAsync(_proxiFyre, cancellationToken).ConfigureAwait(false);
            var openVpnInitiallyRunning = _openVpn is not null && await IsRunningAsync(_openVpn, cancellationToken).ConfigureAwait(false);

            if (paqetInitiallyRunning && xrayInitiallyRunning && proxiFyreInitiallyRunning && (_openVpn is null || openVpnInitiallyRunning))
            {
                return;
            }

            var openVpnStartAttempted = false;
            var proxiFyreStartAttempted = false;

            // Paqet and Xray start together: Xray only dials Paqet when traffic arrives.
            // ProxiFyre starts last so application capture is only enabled once the
            // complete local chain behind it is running.
            stageStarted?.Invoke(ConnectionStage.StartingTransport);
            var (carrierEngine, carrierError) = await StartCarriersAsync(
                    paqetInitiallyRunning ? null : _paqet,
                    xrayInitiallyRunning ? null : _xray,
                    () => stageStarted?.Invoke(ConnectionStage.StartingPolicy),
                    cancellationToken)
                .ConfigureAwait(false);
            var failedEngine = carrierEngine;
            var error = carrierError;

            if (error is null && _openVpn is not null && !openVpnInitiallyRunning)
            {
                try
                {
                    stageStarted?.Invoke(ConnectionStage.StartingVpn);
                    openVpnStartAttempted = true;
                    await StartAndVerifyAsync(_openVpn, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception openVpnError)
                {
                    failedEngine = EngineKind.OpenVpn;
                    error = openVpnError;
                }
            }

            if (error is null)
            {
                try
                {
                    if (!proxiFyreInitiallyRunning)
                    {
                        stageStarted?.Invoke(ConnectionStage.StartingCapture);
                        proxiFyreStartAttempted = true;
                        await StartAndVerifyAsync(_proxiFyre, cancellationToken).ConfigureAwait(false);
                    }
                }
                catch (Exception proxiFyreError)
                {
                    failedEngine = EngineKind.ProxiFyre;
                    error = proxiFyreError;
                }
            }

            if (error is not null)
            {
                var rollbackErrors = await RollBackConnectAsync(
                        !paqetInitiallyRunning,
                        !xrayInitiallyRunning,
                        openVpnStartAttempted,
                        proxiFyreStartAttempted)
                    .ConfigureAwait(false);

                if (error is OperationCanceledException && rollbackErrors.Count == 0)
                {
                    ExceptionDispatchInfo.Throw(error);
                }

                throw new ConnectionTransitionException("connect", failedEngine, error, rollbackErrors);
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

            // Each failure stops the sequence: tearing down the carrier while capture may
            // still be enabled would leave routed applications attached to nothing.
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

            if (_openVpn is not null)
            {
                try
                {
                    await StopAndVerifyAsync(_openVpn, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception error)
                {
                    throw new ConnectionTransitionException("disconnect", EngineKind.OpenVpn, error);
                }
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
                throw new ConnectionTransitionException("guard", EngineKind.ProxiFyre, error);
            }

            if (_openVpn is not null)
            {
                try
                {
                    await StopAndVerifyAsync(_openVpn, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception error)
                {
                    throw new ConnectionTransitionException("guard", EngineKind.OpenVpn, error);
                }
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
                throw new ConnectionTransitionException("guard", EngineKind.Xray, error);
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
                throw new ConnectionTransitionException("guard", EngineKind.Paqet, error);
            }
        }
        finally
        {
            _transitionState = null;
            _transitionGate.Release();
        }
    }

    public ValueTask RestartTransportAsync(CancellationToken cancellationToken) => RestartTransportAsync(includePolicy: false, cancellationToken);

    public async ValueTask RestartTransportAsync(bool includePolicy, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _transitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var running = await IsRunningAsync(_paqet, cancellationToken).ConfigureAwait(false);
            if (!running)
            {
                return;
            }

            _transitionState = ConnectionState.Connecting;

            if (includePolicy)
            {
                await StopAndVerifyAsync(_xray, cancellationToken).ConfigureAwait(false);
            }

            await StopAndVerifyAsync(_paqet, cancellationToken).ConfigureAwait(false);
            await StartAndVerifyAsync(_paqet, cancellationToken).ConfigureAwait(false);

            if (includePolicy)
            {
                await StartAndVerifyAsync(_xray, cancellationToken).ConfigureAwait(false);
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
        (_proxiFyre as IDisposable)?.Dispose();
        (_openVpn as IDisposable)?.Dispose();
        (_xray as IDisposable)?.Dispose();
        (_paqet as IDisposable)?.Dispose();
    }

    private static async ValueTask<bool> IsRunningAsync(
        IEngineAdapter adapter,
        CancellationToken cancellationToken)
    {
        var status = await adapter.GetStatusAsync(cancellationToken).ConfigureAwait(false);
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

    private static async ValueTask<(EngineKind Engine, Exception? Error)> StartCarriersAsync(
        IEngineAdapter? paqet,
        IEngineAdapter? xray,
        Action? transportReady,
        CancellationToken cancellationToken)
    {
        using var chain = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var paqetStart = paqet is null ? Task.CompletedTask : StartInChainAsync(paqet, chain);
        var xrayStart = xray is null ? Task.CompletedTask : StartInChainAsync(xray, chain);

        Exception? paqetError = null;
        Exception? xrayError = null;
        try
        {
            await paqetStart.ConfigureAwait(false);
        }
        catch (Exception error)
        {
            paqetError = error;
        }

        if (paqetError is null)
        {
            transportReady?.Invoke();
        }

        try
        {
            await xrayStart.ConfigureAwait(false);
        }
        catch (Exception error)
        {
            xrayError = error;
        }

        if (paqetError is not null and not OperationCanceledException || xrayError is null)
        {
            return (EngineKind.Paqet, paqetError);
        }

        return xrayError is not OperationCanceledException || paqetError is null
            ? (EngineKind.Xray, xrayError)
            : (EngineKind.Paqet, paqetError);

        static async Task StartInChainAsync(IEngineAdapter adapter, CancellationTokenSource chain)
        {
            try
            {
                await StartAndVerifyAsync(adapter, chain.Token).ConfigureAwait(false);
            }
            catch
            {
                await chain.CancelAsync().ConfigureAwait(false);
                throw;
            }
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
        bool openVpnStartAttempted,
        bool proxiFyreStartAttempted)
    {
        List<Exception>? errors = null;

        if (proxiFyreStartAttempted)
        {
            await TryRollbackStopAsync(_proxiFyre).ConfigureAwait(false);
        }

        if (openVpnStartAttempted && _openVpn is not null)
        {
            await TryRollbackStopAsync(_openVpn).ConfigureAwait(false);
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
        EngineState proxiFyre,
        EngineState? openVpn = null)
    {
        if (paqet == EngineState.NotInstalled || xray == EngineState.NotInstalled || proxiFyre == EngineState.NotInstalled || openVpn == EngineState.NotInstalled)
        {
            return ConnectionState.NotReady;
        }

        var vpnOk = openVpn is null or EngineState.Running;
        if (paqet == EngineState.Running && xray == EngineState.Running && proxiFyre == EngineState.Running && vpnOk)
        {
            return ConnectionState.Connected;
        }

        var vpnStopped = openVpn is null or EngineState.Stopped;
        if (paqet == EngineState.Stopped && xray == EngineState.Stopped && proxiFyre == EngineState.Stopped && vpnStopped)
        {
            return ConnectionState.Disconnected;
        }

        if (paqet == EngineState.Stopped && xray == EngineState.Stopped && proxiFyre == EngineState.Running && vpnStopped)
        {
            return ConnectionState.Guarded;
        }

        if (paqet == EngineState.Faulted || xray == EngineState.Faulted || proxiFyre == EngineState.Faulted || openVpn == EngineState.Faulted)
        {
            return ConnectionState.Faulted;
        }

        if (paqet == EngineState.Starting || xray == EngineState.Starting || proxiFyre == EngineState.Starting || openVpn == EngineState.Starting)
        {
            return ConnectionState.Connecting;
        }

        if (paqet == EngineState.Stopping || xray == EngineState.Stopping || proxiFyre == EngineState.Stopping || openVpn == EngineState.Stopping)
        {
            return ConnectionState.Disconnecting;
        }

        return ConnectionState.Degraded;
    }

    private static string? CreateStatusDetail(
        ConnectionState connection,
        EngineState paqet,
        EngineState xray,
        EngineState proxiFyre,
        EngineState? openVpn = null) => connection switch
        {
            ConnectionState.NotReady => "One or more bundled engines are not installed.",
            ConnectionState.Guarded => "The routing kill switch is active; routed applications cannot connect directly.",
            ConnectionState.Degraded => openVpn is not null
                ? $"Engine states do not form a safe connection (Paqet: {paqet}, Xray: {xray}, OpenVPN: {openVpn}, ProxiFyre: {proxiFyre})."
                : $"Engine states do not form a safe connection (Paqet: {paqet}, Xray: {xray}, ProxiFyre: {proxiFyre}).",
            ConnectionState.Faulted => openVpn is not null
                ? $"An engine is faulted (Paqet: {paqet}, Xray: {xray}, OpenVPN: {openVpn}, ProxiFyre: {proxiFyre})."
                : $"An engine is faulted (Paqet: {paqet}, Xray: {xray}, ProxiFyre: {proxiFyre}).",
            _ => null,
        };
}
