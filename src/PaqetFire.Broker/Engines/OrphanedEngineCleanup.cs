using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace PaqetFire.Broker.Engines;

/// <summary>
/// Stops bundled engines left by a previous broker process. Only executables at the
/// exact installed paths are matched, so other copies of Xray or ProxiFyre are untouched.
/// </summary>
internal static class OrphanedEngineCleanup
{
    private static readonly TimeSpan ExitTimeout = TimeSpan.FromSeconds(5);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "GetLongPathNameW")]
    private static extern uint GetLongPathName(string lpszShortPath, [Out] StringBuilder lpszLongPath, uint cchBuffer);

    public static async ValueTask<IReadOnlyList<string>> TerminateAsync(
        IEnumerable<string> executablePaths,
        CancellationToken cancellationToken)
    {
        var stopped = new List<string>();
        foreach (var path in executablePaths.Select(Normalize).Distinct(StringComparer.OrdinalIgnoreCase))
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
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                var processPath = process.MainModule?.FileName;
                if (string.IsNullOrEmpty(processPath))
                {
                    return false;
                }

                if (string.Equals(processPath, path, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                var normalizedProcessPath = Normalize(processPath);
                return string.Equals(normalizedProcessPath, path, StringComparison.OrdinalIgnoreCase);
            }
            catch (Win32Exception) when (attempt < 4 && !process.HasExited)
            {
                Thread.Sleep(15);
            }
            catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
            {
                return false;
            }
        }

        return false;
    }

    internal static string Normalize(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!OperatingSystem.IsWindows())
        {
            return fullPath;
        }

        try
        {
            var builder = new StringBuilder(1024);
            var length = GetLongPathName(fullPath, builder, (uint)builder.Capacity);
            if (length > builder.Capacity)
            {
                builder.EnsureCapacity((int)length);
                length = GetLongPathName(fullPath, builder, (uint)builder.Capacity);
            }

            if (length > 0)
            {
                return builder.ToString();
            }
        }
        catch
        {
            // Fall back to original fullPath
        }

        return fullPath;
    }
}
