using System.Diagnostics;
using System.Net.Sockets;
using System.Security.Cryptography;
using PaqetFire.Core.Engines;

namespace PaqetFire.Broker.Engines;

public sealed class XrayProcessAdapter : IEngineAdapter, IAsyncDisposable
{
    private readonly XrayProcessOptions options;
    private readonly SemaphoreSlim lifecycleGate = new(1, 1);
    private readonly object stateLock = new();
    private readonly Queue<XrayProcessLogEntry> logs = new();
    private Process? process;
    private EngineState state = EngineState.Stopped;
    private string? detail = "Xray is stopped.";
    private DateTimeOffset changedAt = DateTimeOffset.UtcNow;
    private bool expectedExit;
    private bool disposed;

    public XrayProcessAdapter(XrayProcessOptions options)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public EngineKind Kind => EngineKind.Xray;

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
                    Detail: "The bundled Xray executable was not found.",
                    ChangedAt: changedAt));
            }

            return ValueTask.FromResult(new EngineStatus(
                Kind,
                state,
                options.Version,
                $"{options.InboundEndpoint.Address}:{options.InboundEndpoint.Port}",
                detail,
                changedAt));
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

            await ValidatePayloadAsync(cancellationToken).ConfigureAwait(false);
            if (await CanConnectAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException(
                    $"Xray's local SOCKS5 endpoint {options.InboundEndpoint} is already in use.");
            }

            await ValidateConfigurationAsync(cancellationToken).ConfigureAwait(false);
            SetState(EngineState.Starting, "Waiting for Xray's local policy endpoint.");
            var started = CreateProcess(testOnly: false);
            if (!started.Start())
            {
                throw new InvalidOperationException("Windows did not start the bundled Xray process.");
            }

            started.EnableRaisingEvents = true;
            started.Exited += HandleExited;
            lock (stateLock)
            {
                process = started;
                expectedExit = false;
            }

            _ = PumpAsync(started.StandardOutput);
            _ = PumpAsync(started.StandardError);
            try
            {
                await WaitUntilReadyAsync(started, cancellationToken).ConfigureAwait(false);
                SetState(EngineState.Running, "Xray is applying the regional routing policy.");
            }
            catch
            {
                await TerminateAsync(started).ConfigureAwait(false);
                lock (stateLock)
                {
                    process = null;
                }

                throw;
            }
        }
        catch (OperationCanceledException)
        {
            SetState(EngineState.Stopped, "Xray startup was cancelled.");
            throw;
        }
        catch (Exception error)
        {
            AppendLog(error.Message);
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
            Process? owned;
            lock (stateLock)
            {
                RefreshState();
                owned = process;
                expectedExit = true;
                if (owned is null)
                {
                    SetStateUnsafe(EngineState.Stopped, "Xray is stopped.");
                    return;
                }

                SetStateUnsafe(EngineState.Stopping, "Stopping Xray.");
            }

            await TerminateAsync(owned).ConfigureAwait(false);
            lock (stateLock)
            {
                process = null;
                SetStateUnsafe(EngineState.Stopped, "Xray is stopped.");
            }
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    public IReadOnlyList<XrayProcessLogEntry> GetRecentLogs()
    {
        lock (stateLock)
        {
            return logs.ToArray();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        try
        {
            await StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            disposed = true;
            lifecycleGate.Dispose();
        }
    }

    private async Task ValidatePayloadAsync(CancellationToken cancellationToken)
    {
        foreach (var path in new[]
                 {
                     options.ExecutablePath,
                     options.ConfigurationPath,
                     options.GeoIpPath,
                     options.GeoSitePath,
                 })
        {
            if (!File.Exists(path))
            {
                throw new FileNotFoundException("A required Xray payload file is missing.", path);
            }
        }

        await using var stream = File.OpenRead(options.ExecutablePath);
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
        if (!actual.Equals(options.ExpectedExecutableSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new CryptographicException("The bundled Xray executable failed its SHA-256 integrity check.");
        }
    }

    private async Task ValidateConfigurationAsync(CancellationToken cancellationToken)
    {
        using var validation = CreateProcess(testOnly: true);
        await RunValidationAsync(validation, options.ValidationTimeout, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task RunValidationAsync(
        Process validation,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (!validation.Start())
        {
            throw new InvalidOperationException("Windows could not start Xray's configuration check.");
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        var outputTask = validation.StandardOutput.ReadToEndAsync(deadline.Token);
        var errorTask = validation.StandardError.ReadToEndAsync(deadline.Token);
        try
        {
            await validation.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            var output = (await outputTask.ConfigureAwait(false) + " " + await errorTask.ConfigureAwait(false)).Trim();
            if (validation.ExitCode != 0)
            {
                throw new InvalidDataException($"Xray rejected the generated regional policy: {output}");
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Xray's configuration check exceeded its deadline.");
        }
        finally
        {
            if (!validation.HasExited)
            {
                validation.Kill(entireProcessTree: true);
                using var cleanupDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await validation.WaitForExitAsync(cleanupDeadline.Token).ConfigureAwait(false);
            }

            await deadline.CancelAsync().ConfigureAwait(false);
            try
            {
                await Task.WhenAll(outputTask, errorTask).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Both stream reads are observed after cancellation or timeout.
            }
        }
    }

    private Process CreateProcess(bool testOnly)
    {
        var arguments = $"run {(testOnly ? "-test " : string.Empty)}-config \"{options.ConfigurationPath}\"";
        return new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = options.ExecutablePath,
                Arguments = arguments,
                WorkingDirectory = Path.GetDirectoryName(options.ExecutablePath)!,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
        };
    }

    private async Task WaitUntilReadyAsync(Process started, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.ReadinessTimeout);
        while (!timeout.Token.IsCancellationRequested)
        {
            if (started.HasExited)
            {
                throw new InvalidOperationException($"Xray exited during startup with code {started.ExitCode}.");
            }

            if (await CanConnectAsync(timeout.Token).ConfigureAwait(false))
            {
                return;
            }

            await Task.Delay(150, timeout.Token).ConfigureAwait(false);
        }

        throw new TimeoutException("Xray did not open its local policy endpoint in time.");
    }

    private async Task<bool> CanConnectAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var client = new TcpClient();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMilliseconds(200));
            await client.ConnectAsync(
                options.InboundEndpoint.Address,
                options.InboundEndpoint.Port,
                timeout.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception error) when (error is SocketException or OperationCanceledException)
        {
            return false;
        }
    }

    private async Task PumpAsync(StreamReader reader)
    {
        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            AppendLog(line);
        }
    }

    private void AppendLog(string message)
    {
        lock (stateLock)
        {
            logs.Enqueue(new XrayProcessLogEntry(
                DateTimeOffset.Now,
                message.Length > 2048 ? message[..2048] : message));
            while (logs.Count > 80)
            {
                logs.Dequeue();
            }
        }
    }

    private void HandleExited(object? sender, EventArgs eventArgs)
    {
        lock (stateLock)
        {
            if (sender is not Process exited || process != exited)
            {
                return;
            }

            SetStateUnsafe(
                expectedExit ? EngineState.Stopped : EngineState.Faulted,
                expectedExit ? "Xray is stopped." : $"Xray exited unexpectedly with code {exited.ExitCode}.");
            process = null;
        }
    }

    private void RefreshState()
    {
        if (process is { HasExited: true } exited)
        {
            SetStateUnsafe(
                expectedExit ? EngineState.Stopped : EngineState.Faulted,
                expectedExit ? "Xray is stopped." : $"Xray exited unexpectedly with code {exited.ExitCode}.");
            process = null;
        }
    }

    private void SetState(EngineState next, string? message)
    {
        lock (stateLock)
        {
            SetStateUnsafe(next, message);
        }
    }

    private void SetStateUnsafe(EngineState next, string? message)
    {
        state = next;
        detail = message;
        changedAt = DateTimeOffset.UtcNow;
    }

    private static async Task TerminateAsync(Process process)
    {
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }

        process.Dispose();
    }
}

public sealed record XrayProcessLogEntry(DateTimeOffset OccurredAt, string Message);
