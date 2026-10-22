using System.Runtime.InteropServices;

namespace PaqetFire.Desktop.Services;

/// <summary>Win32 calls needed to bring an existing window to the front from another process.</summary>
internal static class NativeWindow
{
    private const int SwRestore = 9;

    /// <summary>Restores a minimized window and asks Windows to give it the foreground.</summary>
    public static void BringToFront(IntPtr window)
    {
        if (window == IntPtr.Zero)
        {
            return;
        }

        if (IsIconic(window))
        {
            ShowWindow(window, SwRestore);
        }

        SetForegroundWindow(window);
    }

    /// <summary>Lets the running instance take the foreground when this launch hands over to it.</summary>
    public static void AllowForeground(uint processId) => AllowSetForegroundWindow(processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr window, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(uint processId);
}
