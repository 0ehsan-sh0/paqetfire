using System.Diagnostics;
using System.Text;
using PaqetFire.Core.Engines;

namespace PaqetFire.Broker.Engines;

public sealed class OpenVpnProcessAdapter : IEngineAdapter, IDisposable
{
    private static readonly TimeSpan ForceKillTimeout = TimeSpan.FromSeconds(5);
    private readonly OpenVpnProcessOptions options;
    private readonly SemaphoreSlim lifecycleGate = new(1, 1);
    private readonly object stateLock = new();
    private readonly List<(DateTimeOffset OccurredAt, string Message, bool IsError)> _logs = new();
    private Process? process;
    private EventWaitHandle? exitEvent;
    private EngineState state = EngineState.Stopped;
    private string? detail = "OpenVPN is stopped.";
    private DateTimeOffset changedAt = DateTimeOffset.UtcNow;
    private bool expectedExit;
    private bool disposed;

    public OpenVpnProcessAdapter(OpenVpnProcessOptions options)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public EngineKind Kind => EngineKind.OpenVpn;

    public event EventHandler? UnexpectedExit;

    public IReadOnlyList<(DateTimeOffset OccurredAt, string Message, bool IsError)> GetRecentLogs()
    {
        lock (stateLock)
        {
            return [.. _logs];
        }
    }

    public ValueTask<EngineStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(disposed, this);

        lock (stateLock)
        {
            RefreshState();
            if (!File.Exists(options.ExecutablePath))
            {
                return ValueTask.FromResult(new EngineStatus(
                    Kind,
                    EngineState.NotInstalled,
                    options.Version,
                    Detail: "The OpenVPN executable was not found.",
                    ChangedAt: changedAt));
            }

            return ValueTask.FromResult(new EngineStatus(
                Kind,
                state,
                options.Version,
                Detail: detail,
                ChangedAt: changedAt));
        }
    }

    public async ValueTask StartAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (stateLock)
            {
                RefreshState();
                if (state == EngineState.Running)
                {
                    return;
                }
            }

            if (!File.Exists(options.ExecutablePath))
            {
                throw new FileNotFoundException("OpenVPN executable does not exist.", options.ExecutablePath);
            }

            if (!string.IsNullOrEmpty(options.ExpectedExecutableSha256))
            {
                using var stream = File.OpenRead(options.ExecutablePath);
                var actualHash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(stream));
                if (!string.Equals(actualHash, options.ExpectedExecutableSha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new System.Security.Cryptography.CryptographicException(
                        $"OpenVPN executable digest mismatch. Expected {options.ExpectedExecutableSha256}, observed {actualHash}.");
                }
            }

            if (!File.Exists(options.ConfigurationPath))
            {
                throw new FileNotFoundException("OpenVPN configuration does not exist.", options.ConfigurationPath);
            }

            SetState(EngineState.Starting, "Waiting for OpenVPN tunnel initialization.");

            var eventName = $"PaqetFire_OpenVpn_Exit_{Guid.NewGuid():N}";
            var createdEvent = new EventWaitHandle(false, EventResetMode.ManualReset, eventName);

            var startInfo = new ProcessStartInfo
            {
                FileName = options.ExecutablePath,
                Arguments = $"--config \"{options.ConfigurationPath}\" --service \"{eventName}\" 0",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };

            var started = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            if (!started.Start())
            {
                createdEvent.Dispose();
                throw new InvalidOperationException("Windows did not start the OpenVPN process.");
            }

            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            void HandleEarlyExit(object? sender, EventArgs e)
            {
                var exitCode = sender is Process p ? p.ExitCode : -1;
                tcs.TrySetException(new InvalidOperationException($"OpenVPN process exited prematurely with code {exitCode}."));
            }

            started.Exited += HandleEarlyExit;
            started.Exited += HandleExited;
            lock (stateLock)
            {
                process = started;
                exitEvent = createdEvent;
                expectedExit = false;
            }

            void CheckLine(string? line, bool isError)
            {
                if (line is null) return;
                lock (stateLock)
                {
                    if (_logs.Count >= 200)
                    {
                        _logs.RemoveAt(0);
                    }
                    _logs.Add((DateTimeOffset.UtcNow, line, isError));
                }

                if (line.Contains("Initialization Sequence Completed", StringComparison.OrdinalIgnoreCase))
                {
                    tcs.TrySetResult(true);
                }
                else if (line.Contains("AUTH_FAILED", StringComparison.OrdinalIgnoreCase) ||
                         line.Contains("Fatal error", StringComparison.OrdinalIgnoreCase))
                {
                    tcs.TrySetException(new InvalidOperationException($"OpenVPN startup failed: {line}"));
                }
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    while (await started.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line)
                    {
                        CheckLine(line, isError: false);
                    }
                }
                catch
                {
                    // Stream closed
                }
            });

            _ = Task.Run(async () =>
            {
                try
                {
                    while (await started.StandardError.ReadLineAsync().ConfigureAwait(false) is { } line)
                    {
                        CheckLine(line, isError: true);
                    }
                }
                catch
                {
                    // Stream closed
                }
            });

            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(options.ReadinessTimeout);

                await tcs.Task.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
                SetState(EngineState.Running, "OpenVPN tunnel is established.");
            }
            catch
            {
                EventWaitHandle? activeEvent;
                lock (stateLock)
                {
                    expectedExit = true;
                    activeEvent = exitEvent;
                    exitEvent = null;
                }

                try
                {
                    await TerminateAsync(started, activeEvent).ConfigureAwait(false);
                }
                finally
                {
                    lock (stateLock)
                    {
                        process = null;
                    }
                }
                throw;
            }
            finally
            {
                started.Exited -= HandleEarlyExit;
            }
        }
        catch (OperationCanceledException)
        {
            SetState(EngineState.Stopped, "OpenVPN startup was cancelled.");
            throw;
        }
        catch (Exception error)
        {
            SetState(EngineState.Faulted, error.Message);
            throw;
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Process? active;
            EventWaitHandle? activeEvent;
            lock (stateLock)
            {
                active = process;
                activeEvent = exitEvent;
                expectedExit = true;
                process = null;
                exitEvent = null;
            }

            if (active is null)
            {
                activeEvent?.Dispose();
                SetState(EngineState.Stopped, "OpenVPN is stopped.");
                return;
            }

            SetState(EngineState.Stopping, "Stopping OpenVPN.");
            await TerminateAsync(active, activeEvent).ConfigureAwait(false);
            SetState(EngineState.Stopped, "OpenVPN is stopped.");
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        lifecycleGate.Dispose();
        Process? active;
        EventWaitHandle? activeEvent;
        lock (stateLock)
        {
            active = process;
            activeEvent = exitEvent;
            process = null;
            exitEvent = null;
        }
        activeEvent?.Dispose();
        active?.Dispose();
    }

    private void SetState(EngineState newState, string newDetail)
    {
        lock (stateLock)
        {
            state = newState;
            detail = newDetail;
            changedAt = DateTimeOffset.UtcNow;
        }
    }

    private void RefreshState()
    {
        if (process is { HasExited: true } exited)
        {
            if (!expectedExit)
            {
                state = EngineState.Faulted;
                detail = $"OpenVPN exited unexpectedly with code {exited.ExitCode}.";
            }
            process = null;
            exitEvent?.Dispose();
            exitEvent = null;
        }
    }

    private void HandleExited(object? sender, EventArgs e)
    {
        bool unexpected;
        lock (stateLock)
        {
            unexpected = !expectedExit;
            if (unexpected)
            {
                state = EngineState.Faulted;
                detail = sender is Process p ? $"OpenVPN exited unexpectedly with code {p.ExitCode}." : "OpenVPN exited unexpectedly.";
                changedAt = DateTimeOffset.UtcNow;
            }
            exitEvent?.Dispose();
            exitEvent = null;
        }

        if (unexpected)
        {
            CleanupLingeringRoutes();
            UnexpectedExit?.Invoke(this, EventArgs.Empty);
        }
    }

    private static async Task TerminateAsync(Process process, EventWaitHandle? exitEvent)
    {
        if (process.HasExited)
        {
            exitEvent?.Dispose();
            process.Dispose();
            CleanupLingeringRoutes();
            return;
        }

        try
        {
            if (exitEvent is not null)
            {
                try
                {
                    exitEvent.Set();
                }
                catch
                {
                    // Ignore if handle closed or invalid
                }

                using var gracefulCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                try
                {
                    await process.WaitForExitAsync(gracefulCts.Token).ConfigureAwait(false);
                    return;
                }
                catch (OperationCanceledException)
                {
                    // Graceful exit timed out; fall through to force kill
                }
            }

            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().WaitAsync(ForceKillTimeout).ConfigureAwait(false);
        }
        catch
        {
            // Best effort process cleanup
        }
        finally
        {
            exitEvent?.Dispose();
            process.Dispose();
            CleanupLingeringRoutes();
        }
    }

    internal static void CleanupLingeringRoutes()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            // Remove /1 redirect-gateway routes that may have been left behind by OpenVPN if killed or crashed abruptly.
            // 0.0.0.0/1 (mask 128.0.0.0) and 128.0.0.0/1 (mask 128.0.0.0)
            using var proc1 = Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "route.exe"),
                Arguments = "delete 0.0.0.0 mask 128.0.0.0",
                CreateNoWindow = true,
                UseShellExecute = false
            });
            proc1?.WaitForExit(1000);

            using var proc2 = Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "route.exe"),
                Arguments = "delete 128.0.0.0 mask 128.0.0.0",
                CreateNoWindow = true,
                UseShellExecute = false
            });
            proc2?.WaitForExit(1000);
        }
        catch
        {
            // Best effort route cleanup
        }
    }
}
