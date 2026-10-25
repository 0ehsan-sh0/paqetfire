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

            if (!File.Exists(options.ConfigurationPath))
            {
                throw new FileNotFoundException("OpenVPN configuration does not exist.", options.ConfigurationPath);
            }

            SetState(EngineState.Starting, "Waiting for OpenVPN tunnel initialization.");

            var startInfo = new ProcessStartInfo
            {
                FileName = options.ExecutablePath,
                Arguments = $"--config \"{options.ConfigurationPath}\"",
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
                lock (stateLock)
                {
                    expectedExit = true;
                }

                try
                {
                    await TerminateAsync(started).ConfigureAwait(false);
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
            lock (stateLock)
            {
                active = process;
                expectedExit = true;
                process = null;
            }

            if (active is null)
            {
                SetState(EngineState.Stopped, "OpenVPN is stopped.");
                return;
            }

            SetState(EngineState.Stopping, "Stopping OpenVPN.");
            await TerminateAsync(active).ConfigureAwait(false);
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
        lock (stateLock)
        {
            active = process;
            process = null;
        }
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
        }

        if (unexpected)
        {
            UnexpectedExit?.Invoke(this, EventArgs.Empty);
        }
    }

    private static async Task TerminateAsync(Process process)
    {
        if (process.HasExited)
        {
            process.Dispose();
            return;
        }

        try
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().WaitAsync(ForceKillTimeout).ConfigureAwait(false);
        }
        catch
        {
            // Best effort process cleanup
        }
        finally
        {
            process.Dispose();
        }
    }
}
