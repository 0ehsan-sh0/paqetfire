using System.Runtime.InteropServices;
using Microsoft.Windows.AppLifecycle;
using PaqetFire.Desktop.Presentation;
using PaqetFire.Desktop.Services;

namespace PaqetFire.Desktop;

/// <summary>
/// Single-instance entry point. A second launch hands its activation to the running
/// instance (which restores its window) and exits before any UI is created.
/// </summary>
public static class Program
{
    private const string InstanceKey = "PaqetFire.Desktop";

    [STAThread]
    private static int Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        if (RedirectToRunningInstance(args))
        {
            return 0;
        }

        Microsoft.UI.Xaml.Application.Start(_ =>
        {
            var context = new Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(
                Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            new App();
        });
        return 0;
    }

    private static bool RedirectToRunningInstance(string[] args)
    {
        AppInstance keyInstance;
        try
        {
            keyInstance = AppInstance.FindOrRegisterForKey(InstanceKey);
        }
        catch (COMException)
        {
            // Without the lifecycle API the app still works; it just is not single-instance.
            return false;
        }

        if (keyInstance.IsCurrent)
        {
            // Subscribe before the window exists so a launch redirected during startup is
            // queued instead of lost.
            keyInstance.Activated += (_, _) => ActivationRedirect.Signal();
            return false;
        }

        // A sign-in launch while PaqetFire already runs has nothing to show.
        if (StartupCommand.IsBackgroundLaunch(args))
        {
            return true;
        }

        var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
        // Windows only lets a process steal the foreground if the current foreground process
        // (this launch) allows it.
        NativeWindow.AllowForeground(keyInstance.ProcessId);
        // RedirectActivationToAsync must not block the STA thread without pumping COM.
        var redirected = CreateEvent(IntPtr.Zero, true, false, null);
        try
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await keyInstance.RedirectActivationToAsync(activation);
                }
                catch (COMException)
                {
                    // The other instance is exiting; this launch exits as well.
                }
                finally
                {
                    SetEvent(redirected);
                }
            });
            _ = CoWaitForMultipleObjects(CwmoDefault, uint.MaxValue, 1, [redirected], out _);
        }
        finally
        {
            CloseHandle(redirected);
        }

        return true;
    }

    private const uint CwmoDefault = 0;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateEvent(IntPtr attributes, bool manualReset, bool initialState, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetEvent(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("ole32.dll")]
    private static extern int CoWaitForMultipleObjects(
        uint flags,
        uint timeout,
        uint handleCount,
        IntPtr[] handles,
        out uint index);
}
