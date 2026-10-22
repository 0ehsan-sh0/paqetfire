using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using PaqetFire.Desktop.Presentation;
using PaqetFire.Desktop.Services;
using PaqetFire.Desktop.ViewModels;
using WinForms = System.Windows.Forms;

namespace PaqetFire.Desktop;

/// <summary>Notification-area icon: state icon, quick actions, profile switching, and route notifications.</summary>
public sealed partial class MainWindow
{
    private const int MaximumTrayTextLength = 127;

    private WinForms.NotifyIcon? trayIcon;
    private TrayIconFactory? trayIconFactory;
    private TrayIconState? trayIconState;
    private WinForms.ToolStripMenuItem? trayStatusMenuItem;
    private WinForms.ToolStripMenuItem? trayConnectionMenuItem;
    private WinForms.ToolStripMenuItem? trayProfilesMenuItem;
    private TrayBalloonAction balloonAction;
    private bool trayNoticeShown;
    private bool startedInBackground;

    private enum TrayBalloonAction
    {
        None,
        OpenWindow,
        OpenUpdate,
    }

    /// <summary>Starts hidden in the notification area (the sign-in launch).</summary>
    public void StartInBackground()
    {
        startedInBackground = true;
        if (trayIcon is not null)
        {
            trayIcon.Visible = true;
        }

        ViewModel.SetLiveStatsPaused(true);
        _ = InitializeAsync();
    }

    /// <summary>A second launch of PaqetFire was redirected here.</summary>
    public void ShowFromExternalActivation() => RestoreFromTray();

    private void InitializeTrayIcon(string iconPath)
    {
        trayIconFactory = new TrayIconFactory(iconPath);
        var menu = new WinForms.ContextMenuStrip();
        trayStatusMenuItem = new WinForms.ToolStripMenuItem("Status: Checking…")
        {
            Enabled = false,
        };
        trayConnectionMenuItem = new WinForms.ToolStripMenuItem("Connect", null, (_, _) => ToggleConnectionFromTray());
        trayProfilesMenuItem = new WinForms.ToolStripMenuItem("Profiles");
        menu.Items.Add(trayStatusMenuItem);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(trayConnectionMenuItem);
        menu.Items.Add(trayProfilesMenuItem);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("Open PaqetFire", null, (_, _) => RestoreFromTray());
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("Exit PaqetFire", null, (_, _) => ExitFromTray());
        menu.Opening += (_, _) =>
        {
            UpdateTrayState();
            RebuildTrayProfiles();
        };

        trayIcon = new WinForms.NotifyIcon
        {
            Icon = trayIconFactory.Get(TrayIconState.Disconnected),
            Text = "PaqetFire",
            ContextMenuStrip = menu,
            Visible = true,
            BalloonTipTitle = "PaqetFire is still running",
            BalloonTipText = "Click the icon to reopen PaqetFire, or right-click it to connect, switch profiles, or exit.",
        };
        trayIconState = TrayIconState.Disconnected;
        // A single left click opens the window; the right button opens the menu.
        trayIcon.MouseClick += (_, args) =>
        {
            if (args.Button == WinForms.MouseButtons.Left)
            {
                RestoreFromTray();
            }
        };
        trayIcon.BalloonTipClicked += (_, _) =>
        {
            var action = balloonAction;
            balloonAction = TrayBalloonAction.None;
            if (action == TrayBalloonAction.None)
            {
                return;
            }

            RestoreFromTray();
            if (action == TrayBalloonAction.OpenUpdate)
            {
                _ = DispatcherQueue.TryEnqueue(() => NavigateTo("settings"));
            }
        };
        trayIcon.BalloonTipClosed += (_, _) => balloonAction = TrayBalloonAction.None;
        UpdateTrayState();
    }

    private void ShowTrayBalloon(string title, string message, WinForms.ToolTipIcon icon, TrayBalloonAction action)
    {
        if (trayIcon is null || !trayIcon.Visible)
        {
            return;
        }

        balloonAction = action;
        trayIcon.ShowBalloonTip(8000, title, message, icon);
    }

    /// <summary>Tells the user about route changes they did not ask for while the window is hidden.</summary>
    private void NotifyRouteTransition(RouteNotification notification)
    {
        if (AppWindow.IsVisible || !preferences.ShowNotifications)
        {
            return;
        }

        ShowTrayBalloon(
            notification.Title,
            notification.Message,
            notification.Kind is RouteNotificationKind.Recovered ? WinForms.ToolTipIcon.Info : WinForms.ToolTipIcon.Warning,
            TrayBalloonAction.OpenWindow);
        AddActivity($"Notification shown: {notification.Title}.");
    }

    private void RebuildTrayProfiles()
    {
        if (trayProfilesMenuItem is null)
        {
            return;
        }

        trayProfilesMenuItem.DropDownItems.Clear();
        foreach (var profile in ProfileEntries)
        {
            var item = new WinForms.ToolStripMenuItem(profile.DisplayName)
            {
                Checked = profile.IsActive,
                Enabled = !ViewModel.IsBusy && !profile.IsActive,
            };
            var selected = profile;
            item.Click += (_, _) => _ = DispatcherQueue.TryEnqueue(() =>
                RunSafely(() => SwitchProfileAsync(selected, OverviewInfoBar)));
            trayProfilesMenuItem.DropDownItems.Add(item);
        }

        trayProfilesMenuItem.Enabled = trayProfilesMenuItem.DropDownItems.Count > 0;
    }

    private void UpdateTrayState()
    {
        if (trayIcon is null || trayStatusMenuItem is null || trayConnectionMenuItem is null || trayIconFactory is null)
        {
            return;
        }

        var stateText = ViewModel.Progress is { } progress
            ? progress.Stage == PaqetFire.Core.Ipc.ConnectionStage.RecoveringTransport
                ? ConnectionProgressText.CreateTrayText(progress, DateTimeOffset.UtcNow)
                : $"Connecting · {ConnectionProgressText.CreateTrayText(progress, DateTimeOffset.UtcNow)}"
            : ViewModel.IsReconnecting
                ? "Reconnecting to broker…"
            : ViewModel.ConnectionState switch
            {
                BrokerConnectionState.Connected when ViewModel.IsTunnelStalled => "Tunnel not responding",
                BrokerConnectionState.Connected => "Connected",
                BrokerConnectionState.Connecting => "Connecting…",
                BrokerConnectionState.Disconnecting => "Disconnecting…",
                BrokerConnectionState.Guarded => "Kill switch blocking routed apps",
                BrokerConnectionState.NotReady => "Setup required",
                BrokerConnectionState.Degraded => "Connection degraded",
                BrokerConnectionState.Faulted => "Connection fault",
                _ => "Disconnected",
            };
        trayStatusMenuItem.Text = $"Status: {stateText}";

        if (ViewModel.CanCancelConnect)
        {
            trayConnectionMenuItem.Text = "Cancel connecting";
            trayConnectionMenuItem.Enabled = true;
        }
        else if (ViewModel.CanDisconnect)
        {
            trayConnectionMenuItem.Text = "Disconnect";
            trayConnectionMenuItem.Enabled = true;
        }
        else
        {
            trayConnectionMenuItem.Text = "Connect";
            trayConnectionMenuItem.Enabled = ViewModel.CanConnect;
        }

        var text = $"PaqetFire — {stateText}";
        trayIcon.Text = text.Length <= MaximumTrayTextLength ? text : text[..(MaximumTrayTextLength - 1)] + "…";

        var iconState = ViewModel.ConnectionState switch
        {
            BrokerConnectionState.Connected when ViewModel.IsTunnelStalled => TrayIconState.Attention,
            BrokerConnectionState.Connected => TrayIconState.Connected,
            BrokerConnectionState.Connecting or BrokerConnectionState.Disconnecting => TrayIconState.Connecting,
            BrokerConnectionState.Guarded or BrokerConnectionState.Degraded or BrokerConnectionState.NotReady => TrayIconState.Attention,
            BrokerConnectionState.Faulted => TrayIconState.Faulted,
            _ => TrayIconState.Disconnected,
        };
        if (iconState != trayIconState)
        {
            trayIconState = iconState;
            try
            {
                trayIcon.Icon = trayIconFactory.Get(iconState);
            }
            catch (Exception exception) when (exception is ArgumentException or ExternalException or IOException)
            {
                // Keep the previous icon; the tooltip still carries the state.
            }
        }
    }

    private void ToggleConnectionFromTray()
    {
        _ = DispatcherQueue.TryEnqueue(() => RunSafely(async () =>
        {
            if (ViewModel.CanCancelConnect)
            {
                await ViewModel.CancelConnectAsync();
            }
            else if (ViewModel.CanDisconnect)
            {
                await ViewModel.DisconnectAsync();
            }
            else if (ViewModel.CanConnect)
            {
                await ViewModel.ConnectAsync();
            }

            UpdateTrayState();
        }));
    }

    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (exitRequested || !preferences.MinimizeToTray)
        {
            return;
        }

        args.Cancel = true;
        HideToTray();
        if (trayIcon is not null && !trayNoticeShown)
        {
            trayNoticeShown = true;
            balloonAction = TrayBalloonAction.None;
            trayIcon.ShowBalloonTip(2500);
        }

        AddActivity("Window hidden to the notification area.");
    }

    private void HideToTray()
    {
        AppWindow.Hide();
        if (trayIcon is not null)
        {
            trayIcon.Visible = true;
        }

        // Nobody is looking at the live session card; lifecycle changes still arrive as events.
        ViewModel.SetLiveStatsPaused(true);
    }

    private void RestoreFromTray()
    {
        _ = DispatcherQueue.TryEnqueue(() =>
        {
            ViewModel.SetLiveStatsPaused(false);
            AppWindow.Show();
            if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
            {
                presenter.Restore();
            }

            Activate();
            NativeWindow.BringToFront(WinRT.Interop.WindowNative.GetWindowHandle(this));
            if (!initialized)
            {
                _ = InitializeAsync();
            }
        });
    }

    private void ExitFromTray()
    {
        _ = DispatcherQueue.TryEnqueue(() =>
        {
            exitRequested = true;
            if (trayIcon is not null)
            {
                trayIcon.Visible = false;
            }

            Close();
        });
    }

    private void DisposeTrayIcon()
    {
        if (trayIcon is not null)
        {
            trayIcon.Visible = false;
            trayIcon.ContextMenuStrip?.Dispose();
            trayIcon.Dispose();
            trayIcon = null;
        }

        trayIconFactory?.Dispose();
        trayIconFactory = null;
    }
}
