using System.ComponentModel;
using System.Diagnostics;

namespace PaqetFire.Broker.Engines;

/// <summary>
/// Stops bundled engines left by a previous broker process. Only executables at the
/// exact installed paths are matched, so other copies of Xray or ProxiFyre are untouched.
/// </summary>
internal static class OrphanedEngineCleanup
{
    private static readonly TimeSpan ExitTimeout = TimeSpan.FromSeconds(5);

    public static async ValueTask<IReadOnlyList<string>> TerminateAsync(
        IEnumerable<string> executablePaths,
        CancellationToken cancellationToken)
    {
        var stopped = new List<string>();
        foreach (var path in executablePaths.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(path)))
            {
                using (process)
                {
                    if (!IsAt(process, path))
                    {
                        continue;
                    }

                    try
                    {
                        process.Kill(entireProcessTree: true);
                        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                        deadline.CancelAfter(ExitTimeout);
                        await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
                        stopped.Add(Path.GetFileName(path));
                    }
                    catch (Exception exception) when (exception is InvalidOperationException or Win32Exception ||
                                                          exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
                    {
                        // The process exited on its own, or will be reported by the next start attempt.
                    }
                }
            }
        }

        return stopped;
    }

    private static bool IsAt(Process process, string path)
    {
        try
        {
            return string.Equals(process.MainModule?.FileName, path, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }
}
