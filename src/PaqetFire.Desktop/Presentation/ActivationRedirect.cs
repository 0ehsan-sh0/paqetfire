namespace PaqetFire.Desktop.Presentation;

/// <summary>
/// Hands "show the window" requests from redirected launches to the window. A request that
/// arrives before the window exists is kept and delivered when the window attaches.
/// </summary>
public static class ActivationRedirect
{
    private static readonly object Sync = new();
    private static Action? target;
    private static bool pending;

    public static void Signal()
    {
        Action? show;
        lock (Sync)
        {
            show = target;
            pending = show is null;
        }

        show?.Invoke();
    }

    public static void Attach(Action show)
    {
        ArgumentNullException.ThrowIfNull(show);
        bool deliver;
        lock (Sync)
        {
            target = show;
            deliver = pending;
            pending = false;
        }

        if (deliver)
        {
            show();
        }
    }

    /// <summary>For tests: forget the target and any pending request.</summary>
    public static void Reset()
    {
        lock (Sync)
        {
            target = null;
            pending = false;
        }
    }
}
