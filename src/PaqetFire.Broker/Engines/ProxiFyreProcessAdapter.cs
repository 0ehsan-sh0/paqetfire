using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;
using PaqetFire.Core.Engines;

namespace PaqetFire.Broker.Engines;

public sealed class ProxiFyreProcessAdapter : IEngineAdapter, IAsyncDisposable
{
    public const string WindowsPacketFilterServiceName = "NDISRD";

    private static readonly TimeSpan ForceKillTimeout = TimeSpan.FromSeconds(5);

    // ProxiFyre logs this through NLog once its redirector is installed, about a second
    // after "ProxiFyre Service is running...", which is printed before routing works.
    private const string ReadyMarker = "Local Router instance started successfully";

    // Without the marker, a process that survives this long has initialized routing.
    private static readonly TimeSpan FallbackStablePeriod = TimeSpan.FromSeconds(2);

    // After the marker, a short wait still catches an immediate crash on bad rules.
    private static readonly TimeSpan ReadyStablePeriod = TimeSpan.FromMilliseconds(400);

    private readonly ProxiFyreProcessOptions options;
    private readonly Func<CancellationToken, Task>? beforeLaunch;
    private readonly SemaphoreSlim lifecycleGate = new(1, 1);
    private readonly object stateLock = new();
    private readonly Queue<string> logs = new();
    private Process? process;
    private EngineState state = EngineState.Stopped;
    private string? detail = "ProxiFyre is stopped.";
    private DateTimeOffset changedAt = DateTimeOffset.UtcNow;
    private bool expectedExit;
    private bool disposed;
    private volatile bool readyMarkerSeen;

    /// <param name="beforeLaunch">
    /// Awaited immediately before each launch, for work that must finish before capture
    /// starts, such as restoring the firewall rules ProxiFyre's redirected traffic needs.
    /// </param>
    public ProxiFyreProcessAdapter(
        ProxiFyreProcessOptions options,
        Func<CancellationToken, Task>? beforeLaunch = null)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.beforeLaunch = beforeLaunch;
    }

    public EngineKind Kind => EngineKind.ProxiFyre;

    /// <summary>Raised when the running process exits without being stopped, so recovery need not wait for a poll.</summary>
    public event EventHandler? UnexpectedExit;

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
                    Detail: "The bundled ProxiFyre executable was not found.",
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

            HardenInstallationDirectory();
            EnsureWindowsPacketFilterInstalled();
            if (beforeLaunch is not null)
            {
                await beforeLaunch(cancellationToken).ConfigureAwait(false);
            }

            await using var executableLock = await OpenVerifiedExecutableAsync(cancellationToken).ConfigureAwait(false);
            // A ProxiFyre left by an earlier broker keeps routed applications blocked until
            // this moment; it is replaced only now, so that gap stays as short as possible.
            await OrphanedEngineCleanup.TerminateAsync([options.ExecutablePath], cancellationToken).ConfigureAwait(false);
            SetState(EngineState.Starting, "Waiting for ProxiFyre to initialize application routing.");

            var log = new ProxiFyreLogTail(Path.GetDirectoryName(options.ExecutablePath)!);
            readyMarkerSeen = false;
            var started = CreateProcess();
            if (!started.Start())
            {
                throw new InvalidOperationException("Windows did not start the bundled ProxiFyre process.");
            }

            if (executableLock is not null)
            {
                await executableLock.DisposeAsync().ConfigureAwait(false);
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
                await WaitUntilReadyAsync(started, log, cancellationToken).ConfigureAwait(false);
                SetState(EngineState.Running, "ProxiFyre is routing selected application traffic.");
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
        }
        catch (OperationCanceledException)
        {
            SetState(EngineState.Stopped, "ProxiFyre startup was cancelled.");
            throw;
        }
        catch (Exception error)
        {
            AppendLog(error.Message);
            SetState(EngineState.Faulted, BuildFailureDetail(error));
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
                if (owned is not null)
                {
                    SetStateUnsafe(EngineState.Stopping, "Stopping ProxiFyre.");
                }
            }

            if (owned is null)
            {
                // A ProxiFyre kept from an earlier broker for the kill switch still captures
                // traffic; "stopped" must mean that no instance is blocking applications.
                await OrphanedEngineCleanup.TerminateAsync([options.ExecutablePath], CancellationToken.None)
                    .ConfigureAwait(false);
                SetState(EngineState.Stopped, "ProxiFyre is stopped.");
                return;
            }

            try
            {
                await TerminateAsync(owned).ConfigureAwait(false);
            }
            catch (System.TimeoutException error)
            {
                lock (stateLock)
                {
                    process = null;
                    SetStateUnsafe(EngineState.Faulted, error.Message);
                }

                throw;
            }

            lock (stateLock)
            {
                process = null;
                SetStateUnsafe(EngineState.Stopped, "ProxiFyre is stopped.");
            }
        }
        finally
        {
            lifecycleGate.Release();
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

    private Process CreateProcess() => new()
    {
        StartInfo = new ProcessStartInfo
        {
            FileName = options.ExecutablePath,
            Arguments = "run",
            WorkingDirectory = Path.GetDirectoryName(options.ExecutablePath)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        },
    };

    internal async Task ValidatePayloadAsync(CancellationToken cancellationToken)
    {
        await using var executableLock = await OpenVerifiedExecutableAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <returns>A handle on the verified executable to hold until the process has started.</returns>
    private async Task<FileStream?> OpenVerifiedExecutableAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(options.ExecutablePath))
        {
            throw new FileNotFoundException("The bundled ProxiFyre executable is missing.", options.ExecutablePath);
        }

        if (!File.Exists(options.ConfigurationPath))
        {
            throw new FileNotFoundException("The generated ProxiFyre configuration is missing.", options.ConfigurationPath);
        }

        if (string.IsNullOrWhiteSpace(options.ExpectedExecutableSha256))
        {
            return null;
        }

        return await VerifiedExecutable.OpenAsync(
                options.ExecutablePath,
                options.ExpectedExecutableSha256,
                () => new CryptographicException("The bundled ProxiFyre executable failed its SHA-256 integrity check."),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private void HardenInstallationDirectory()
    {
        var installationDirectory = Path.GetDirectoryName(options.ExecutablePath)
            ?? throw new InvalidOperationException("The ProxiFyre executable has no installation directory.");
        var inheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(CreateDirectoryRule(
            WellKnownSidType.LocalSystemSid,
            FileSystemRights.FullControl,
            inheritance));
        security.AddAccessRule(CreateDirectoryRule(
            WellKnownSidType.BuiltinAdministratorsSid,
            FileSystemRights.FullControl,
            inheritance));
        security.AddAccessRule(CreateDirectoryRule(
            WellKnownSidType.BuiltinUsersSid,
            FileSystemRights.ReadAndExecute,
            inheritance));
        new DirectoryInfo(installationDirectory).SetAccessControl(security);
    }

    private static FileSystemAccessRule CreateDirectoryRule(
        WellKnownSidType sidType,
        FileSystemRights rights,
        InheritanceFlags inheritance) =>
        new(
            new SecurityIdentifier(sidType, domainSid: null),
            rights,
            inheritance,
            PropagationFlags.None,
            AccessControlType.Allow);

    private static void EnsureWindowsPacketFilterInstalled()
    {
        try
        {
            using var driver = new ServiceController(WindowsPacketFilterServiceName);
            driver.Refresh();
            _ = driver.Status;
        }
        catch (InvalidOperationException exception) when (FindWin32Exception(exception)?.NativeErrorCode == 1060)
        {
            throw new InvalidOperationException(
                "Windows Packet Filter is not installed. Install the bundled NDISRD driver before starting routing.",
                exception);
        }
    }

    private async Task WaitUntilReadyAsync(Process started, ProxiFyreLogTail log, CancellationToken cancellationToken)
    {
        var elapsed = Stopwatch.StartNew();
        TimeSpan? markerAt = null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.ReadinessTimeout);
        while (!timeout.Token.IsCancellationRequested)
        {
            if (started.HasExited)
            {
                throw new InvalidOperationException($"ProxiFyre exited during startup with code {started.ExitCode}.");
            }

            if (markerAt is null && (readyMarkerSeen || log.Contains(ReadyMarker)))
            {
                markerAt = elapsed.Elapsed;
            }

            if (markerAt is { } seen && elapsed.Elapsed - seen >= ReadyStablePeriod ||
                elapsed.Elapsed >= FallbackStablePeriod)
            {
                return;
            }

            await Task.Delay(options.ProbeInterval, timeout.Token).ConfigureAwait(false);
        }

        throw new System.TimeoutException("ProxiFyre did not remain active long enough to initialize routing.");
    }

    private async Task PumpAsync(StreamReader reader)
    {
        try
        {
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                if (line.Contains(ReadyMarker, StringComparison.OrdinalIgnoreCase))
                {
                    readyMarkerSeen = true;
                }

                AppendLog(line);
            }
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException)
        {
            // Process teardown can close redirected streams while a read is pending.
        }
    }

    private void AppendLog(string message)
    {
        lock (stateLock)
        {
            logs.Enqueue(message.Length > options.MaximumLogLineLength
                ? message[..options.MaximumLogLineLength]
                : message);
            while (logs.Count > options.MaximumLogEntries)
            {
                logs.Dequeue();
            }
        }
    }

    private string BuildFailureDetail(Exception error)
    {
        lock (stateLock)
        {
            var lastLine = logs.LastOrDefault(line => !string.IsNullOrWhiteSpace(line));
            return lastLine is null ? error.Message : $"{error.Message} Last output: {lastLine}";
        }
    }

    private void HandleExited(object? sender, EventArgs eventArgs)
    {
        bool unexpected;
        lock (stateLock)
        {
            if (sender is not Process exited || process != exited)
            {
                return;
            }

            unexpected = !expectedExit;
            SetStateUnsafe(
                expectedExit ? EngineState.Stopped : EngineState.Faulted,
                expectedExit ? "ProxiFyre is stopped." : $"ProxiFyre exited unexpectedly with code {exited.ExitCode}.");
            process = null;
        }

        if (unexpected)
        {
            UnexpectedExit?.Invoke(this, EventArgs.Empty);
        }
    }

    private void RefreshState()
    {
        if (process is { HasExited: true } exited)
        {
            SetStateUnsafe(
                expectedExit ? EngineState.Stopped : EngineState.Faulted,
                expectedExit ? "ProxiFyre is stopped." : $"ProxiFyre exited unexpectedly with code {exited.ExitCode}.");
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

    private static async Task TerminateAsync(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                // Bounded like Paqet's stop: callers hold lifecycle gates, so a process that
                // ignores termination must surface as a fault rather than a hung broker.
                using var timeout = new CancellationTokenSource(ForceKillTimeout);
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
        }
        catch (InvalidOperationException)
        {
            // The process exited between the state check and termination.
        }
        catch (OperationCanceledException error)
        {
            throw new System.TimeoutException("The ProxiFyre process tree did not terminate in time.", error);
        }
        finally
        {
            process.Dispose();
        }
    }
}

/// <summary>
/// Follows ProxiFyre's NLog file (<c>logs\logfile_yyyy-MM-dd.txt</c> beside the executable)
/// from the size it had at launch. ProxiFyre writes its routing status there, not to stdout.
/// </summary>
internal sealed class ProxiFyreLogTail
{
    private readonly string directory;
    private string fileName;
    private long offset;
    private string pending = string.Empty;

    public ProxiFyreLogTail(string installationDirectory)
    {
        directory = Path.Combine(installationDirectory, "logs");
        fileName = CurrentFileName();
        offset = TryGetLength(Path.Combine(directory, fileName));
    }

    public bool Contains(string marker)
    {
        try
        {
            var current = CurrentFileName();
            if (!string.Equals(current, fileName, StringComparison.Ordinal))
            {
                // NLog rolled over at local midnight; the new file is entirely new output.
                fileName = current;
                offset = 0;
                pending = string.Empty;
            }

            var path = Path.Combine(directory, fileName);
            if (!File.Exists(path))
            {
                return false;
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length <= offset)
            {
                return false;
            }

            stream.Seek(offset, SeekOrigin.Begin);
            using var reader = new StreamReader(stream);
            var appended = reader.ReadToEnd();
            offset = stream.Length;
            // Keep a tail so a marker split across two reads is still found.
            var text = pending + appended;
            pending = text.Length > marker.Length ? text[^marker.Length..] : text;
            return text.Contains(marker, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string CurrentFileName() => $"logfile_{DateTime.Now:yyyy-MM-dd}.txt";

    private static long TryGetLength(string path)
    {
        try
        {
            return File.Exists(path) ? new FileInfo(path).Length : 0;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }
}
