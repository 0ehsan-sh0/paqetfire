using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace PaqetFire.Desktop.Services;

public enum TrayIconState
{
    Disconnected,
    Connecting,
    Connected,
    Attention,
    Faulted,
}

/// <summary>
/// Builds notification-area icons from the app icon with a state badge in the corner,
/// so the route state is visible without opening the window. Shape differs per state
/// as well as colour, so it does not rely on colour alone.
/// </summary>
public sealed class TrayIconFactory(string iconPath) : IDisposable
{
    private readonly Dictionary<TrayIconState, Icon> icons = [];

    public Icon Get(TrayIconState state)
    {
        if (icons.TryGetValue(state, out var cached))
        {
            return cached;
        }

        var icon = Create(state);
        icons[state] = icon;
        return icon;
    }

    public void Dispose()
    {
        foreach (var icon in icons.Values)
        {
            icon.Dispose();
        }

        icons.Clear();
    }

    private Icon Create(TrayIconState state)
    {
        var size = System.Windows.Forms.SystemInformation.SmallIconSize;
        using var source = new Icon(iconPath, size);
        using var bitmap = new Bitmap(size.Width, size.Height);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.DrawIcon(source, new Rectangle(Point.Empty, size));
            var badge = Math.Max(6, size.Width / 2);
            var bounds = new Rectangle(size.Width - badge, size.Height - badge, badge - 1, badge - 1);
            using var outline = new Pen(Color.White, Math.Max(1, size.Width / 16f));
            using var fill = new SolidBrush(state switch
            {
                TrayIconState.Connected => Color.FromArgb(0x2F, 0xCB, 0x8C),
                TrayIconState.Connecting => Color.FromArgb(0x3B, 0x82, 0xF6),
                TrayIconState.Attention => Color.FromArgb(0xF5, 0xA5, 0x24),
                TrayIconState.Faulted => Color.FromArgb(0xE5, 0x48, 0x4D),
                _ => Color.FromArgb(0x8A, 0x8F, 0x98),
            });
            switch (state)
            {
                case TrayIconState.Attention or TrayIconState.Faulted:
                    // A diamond reads as a warning even in high-contrast or for colour-blind users.
                    Point[] diamond =
                    [
                        new(bounds.Left + bounds.Width / 2, bounds.Top),
                        new(bounds.Right, bounds.Top + bounds.Height / 2),
                        new(bounds.Left + bounds.Width / 2, bounds.Bottom),
                        new(bounds.Left, bounds.Top + bounds.Height / 2),
                    ];
                    graphics.FillPolygon(fill, diamond);
                    graphics.DrawPolygon(outline, diamond);
                    break;
                case TrayIconState.Connecting:
                    graphics.FillPie(fill, bounds, -90, 270);
                    graphics.DrawEllipse(outline, bounds);
                    break;
                case TrayIconState.Disconnected:
                    using (var hollow = new Pen(fill.Color, Math.Max(1.5f, size.Width / 10f)))
                    {
                        graphics.FillEllipse(Brushes.White, bounds);
                        graphics.DrawEllipse(hollow, bounds);
                    }

                    break;
                default:
                    graphics.FillEllipse(fill, bounds);
                    graphics.DrawEllipse(outline, bounds);
                    break;
            }
        }

        var handle = bitmap.GetHicon();
        try
        {
            using var temporary = Icon.FromHandle(handle);
            // Clone so the icon owns its handle and the GDI handle can be freed now.
            return (Icon)temporary.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);
}
