using System.ComponentModel;
using System.Diagnostics;
using System.ServiceProcess;
using Microsoft.Extensions.Logging;
using PaqetFire.Core.Engines;

namespace PaqetFire.Broker.Engines;

public sealed class ProxiFyreServiceAdapter : IEngineAdapter
{
    public const string WindowsServiceName = "ProxiFyreService";
    public const string WindowsPacketFilterServiceName = "NDISRD";

    private static readonly TimeSpan DefaultOperationTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaximumOperationTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(200);

    private readonly ILogger<ProxiFyreServiceAdapter> _logger;
    private readonly TimeSpan _operationTimeout;
    private readonly string? _engineVersion;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);

    public ProxiFyreServiceAdapter(
        ILogger<ProxiFyreServiceAdapter> logger,
        TimeSpan? operationTimeout = null,
        string? engineVersion = null)
    {
        ArgumentNullException.ThrowIfNull(logger);
        var timeout = operationTimeout ?? DefaultOperationTimeout;
        if (timeout <= TimeSpan.Zero || timeout > MaximumOperationTimeout)
        {
            throw new ArgumentOutOfRangeException(
                nameof(operationTimeout),
                $"The service-operation timeout must be positive and no greater than {MaximumOperationTimeout}.");
        }

        _logger = logger;
        _operationTimeout = timeout;
        _engineVersion = string.IsNullOrWhiteSpace(engineVersion)
            ? null
            : engineVersion.Trim();
    }

    public EngineKind Kind => EngineKind.ProxiFyre;

    public ValueTask<EngineStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            using var service = OpenServiceController();
            var status = ReadStatus(service);
            return ValueTask.FromResult(new EngineStatus(
                Kind,
                MapStatus(status),
                Version: _engineVersion,
                Detail: GetStatusDetail(status)));
        }
        catch (Exception exception) when (IsServiceMissing(exception))
        {
            return ValueTask.FromResult(new EngineStatus(
                Kind,
                EngineState.NotInstalled,
                Detail: "The bundled ProxiFyre service is not installed."));
        }
        catch (Exception exception) when (IsExpectedServiceException(exception))
        {
            _logger.LogWarning(
                exception,
                "Unable to query the fixed ProxiFyre Windows service.");
            return ValueTask.FromResult(new EngineStatus(
                Kind,
                EngineState.Faulted,
                Detail: "PaqetFire could not read the ProxiFyre service state."));
        }
    }

    public async ValueTask StartAsync(CancellationToken cancellationToken)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StartSerializedAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StopSerializedAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private async Task StartSerializedAsync(CancellationToken cancellationToken)
    {
        var startedAt = Stopwatch.GetTimestamp();
        try
        {
            using var service = OpenServiceController();
            var status = ReadStatus(service);

            if (status == ServiceControllerStatus.Running)
            {
                return;
            }

            EnsureWindowsPacketFilterInstalled();

            if (status == ServiceControllerStatus.StopPending)
            {
                await WaitForStatusAsync(
                        service,
                        ServiceControllerStatus.Stopped,
                        "start",
                        startedAt,
                        cancellationToken)
                    .ConfigureAwait(false);
                status = ServiceControllerStatus.Stopped;
            }

            if (status == ServiceControllerStatus.PausePending)
            {
                await WaitForStatusAsync(
                        service,
                        ServiceControllerStatus.Paused,
                        "start",
                        startedAt,
                        cancellationToken)
                    .ConfigureAwait(false);
                status = ServiceControllerStatus.Paused;
            }

            switch (status)
            {
                case ServiceControllerStatus.Stopped:
                    service.Start();
                    break;
                case ServiceControllerStatus.Paused:
                    service.Continue();
                    break;
                case ServiceControllerStatus.StartPending:
                case ServiceControllerStatus.ContinuePending:
                    break;
                default:
                    throw CreateFailure(
                        "start",
                        "ProxiFyre is in a state that cannot be started safely.");
            }

            await WaitForStatusAsync(
                    service,
                    ServiceControllerStatus.Running,
                    "start",
                    startedAt,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ProxiFyreServiceException)
        {
            throw;
        }
        catch (Exception exception) when (IsServiceMissing(exception))
        {
            throw CreateFailure("start", "The bundled ProxiFyre service is not installed.");
        }
        catch (Exception exception) when (IsExpectedServiceException(exception))
        {
            _logger.LogError(exception, "The ProxiFyre Windows service failed to start.");
            throw CreateFailure(
                "start",
                "ProxiFyre could not be started. Review the protected broker and engine logs.");
        }
    }

    private async Task StopSerializedAsync(CancellationToken cancellationToken)
    {
        var startedAt = Stopwatch.GetTimestamp();
        try
        {
            using var service = OpenServiceController();
            var status = ReadStatus(service);

            if (status == ServiceControllerStatus.Stopped)
            {
                return;
            }

            if (status == ServiceControllerStatus.StartPending ||
                status == ServiceControllerStatus.ContinuePending)
            {
                await WaitForStatusAsync(
                        service,
                        ServiceControllerStatus.Running,
                        "stop",
                        startedAt,
                        cancellationToken)
                    .ConfigureAwait(false);
                status = ServiceControllerStatus.Running;
            }
            else if (status == ServiceControllerStatus.PausePending)
            {
                await WaitForStatusAsync(
                        service,
                        ServiceControllerStatus.Paused,
                        "stop",
                        startedAt,
                        cancellationToken)
                    .ConfigureAwait(false);
                status = ServiceControllerStatus.Paused;
            }

            if (status != ServiceControllerStatus.StopPending)
            {
                if (!service.CanStop)
                {
                    throw CreateFailure(
                        "stop",
                        "The ProxiFyre service does not currently accept stop requests.");
                }

                service.Stop();
            }

            await WaitForStatusAsync(
                    service,
                    ServiceControllerStatus.Stopped,
                    "stop",
                    startedAt,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ProxiFyreServiceException)
        {
            throw;
        }
        catch (Exception exception) when (IsServiceMissing(exception))
        {
            return;
        }
        catch (Exception exception) when (IsExpectedServiceException(exception))
        {
            _logger.LogError(exception, "The ProxiFyre Windows service failed to stop.");
            throw CreateFailure(
                "stop",
                "ProxiFyre could not be stopped. Review the protected broker and engine logs.");
        }
    }

    private async Task WaitForStatusAsync(
        ServiceController service,
        ServiceControllerStatus desiredStatus,
        string operation,
        long operationStartedAt,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var currentStatus = ReadStatus(service);
            if (currentStatus == desiredStatus)
            {
                return;
            }

            if (desiredStatus == ServiceControllerStatus.Running &&
                currentStatus == ServiceControllerStatus.Stopped)
            {
                throw CreateFailure(
                    operation,
                    "ProxiFyre stopped before reaching the running state. Review the protected engine logs.");
            }

            var remaining = _operationTimeout - Stopwatch.GetElapsedTime(operationStartedAt);
            if (remaining <= TimeSpan.Zero)
            {
                throw CreateFailure(
                    operation,
                    $"ProxiFyre did not complete the {operation} operation within the configured time limit.");
            }

            await Task.Delay(
                    remaining < PollInterval ? remaining : PollInterval,
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static ServiceController OpenServiceController() => new(WindowsServiceName);

    private static void EnsureWindowsPacketFilterInstalled()
    {
        try
        {
            using var driver = new ServiceController(WindowsPacketFilterServiceName);
            _ = ReadStatus(driver);
        }
        catch (Exception exception) when (IsServiceMissing(exception))
        {
            throw CreateFailure(
                "start",
                "Windows Packet Filter is not installed. Install the bundled NDISRD driver before starting routing.");
        }
    }

    private static ServiceControllerStatus ReadStatus(ServiceController service)
    {
        service.Refresh();
        return service.Status;
    }

    private static EngineState MapStatus(ServiceControllerStatus status) => status switch
    {
        ServiceControllerStatus.Stopped => EngineState.Stopped,
        ServiceControllerStatus.StartPending => EngineState.Starting,
        ServiceControllerStatus.ContinuePending => EngineState.Starting,
        ServiceControllerStatus.Running => EngineState.Running,
        ServiceControllerStatus.StopPending => EngineState.Stopping,
        ServiceControllerStatus.PausePending => EngineState.Faulted,
        ServiceControllerStatus.Paused => EngineState.Faulted,
        _ => EngineState.Faulted,
    };

    private static string? GetStatusDetail(ServiceControllerStatus status) => status switch
    {
        ServiceControllerStatus.Paused => "The ProxiFyre service is paused.",
        ServiceControllerStatus.PausePending => "The ProxiFyre service is entering a paused state.",
        _ => null,
    };

    private static ProxiFyreServiceException CreateFailure(string operation, string message) =>
        new(operation, message);

    private static bool IsServiceMissing(Exception exception) =>
        FindWin32Exception(exception)?.NativeErrorCode == 1060;

    private static bool IsExpectedServiceException(Exception exception) =>
        exception is InvalidOperationException or Win32Exception or System.ServiceProcess.TimeoutException;

    private static Win32Exception? FindWin32Exception(Exception? exception)
    {
        while (exception is not null)
        {
            if (exception is Win32Exception win32Exception)
            {
                return win32Exception;
            }

            exception = exception.InnerException;
        }

        return null;
    }
}
