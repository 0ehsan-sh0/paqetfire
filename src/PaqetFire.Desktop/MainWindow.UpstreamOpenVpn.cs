using System.IO;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PaqetFire.Core.Configuration;

namespace PaqetFire.Desktop;

public sealed partial class MainWindow
{
    private bool hasSavedUpstreamOpenVpnPassword;
    private string currentOvpnConfigText = string.Empty;

    private void UpstreamOpenVpnToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (UpstreamOpenVpnOptionsPanel is not null)
        {
            UpstreamOpenVpnOptionsPanel.Visibility = UpstreamOpenVpnToggle.IsOn ? Visibility.Visible : Visibility.Collapsed;
        }

        if (UpstreamOpenVpnPasswordBox is not null && hasSavedUpstreamOpenVpnPassword) { UpstreamOpenVpnPasswordBox.PlaceholderText = "Saved securely · leave blank to keep it"; }
        ConfigurationEdited();
    }

    private void OpenProtonVpnGuide_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "https://protonvpn.com/support/openvpn-windows-setup",
                UseShellExecute = true,
            });
        }
        catch
        {
            // Ignore if default browser cannot be launched
        }
    }

    private async void ImportOvpnFileButton_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.OpenFileDialog
        {
            Filter = "OpenVPN Configuration (*.ovpn)|*.ovpn|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false,
        };

        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK)
        {
            return;
        }

        try
        {
            var info = new FileInfo(dialog.FileName);
            if (info.Length > 2 * 1024 * 1024)
            {
                if (Content?.XamlRoot is not null)
                {
                    var tooLargeDialog = new ContentDialog
                    {
                        Title = "File too large",
                        Content = "The selected .ovpn file exceeds 2 MB.",
                        CloseButtonText = "OK",
                        XamlRoot = Content.XamlRoot,
                    };
                    await ShowDialogAsync(tooLargeDialog);
                }
                return;
            }

            var text = await File.ReadAllTextAsync(dialog.FileName);
            currentOvpnConfigText = text;
            UpdateUpstreamOpenVpnSummary();
            ConfigurationEdited();
        }
        catch (Exception ex)
        {
            if (Content?.XamlRoot is not null)
            {
                var errorDialog = new ContentDialog
                {
                    Title = "Failed to import configuration",
                    Content = ex.Message,
                    CloseButtonText = "OK",
                    XamlRoot = Content.XamlRoot,
                };
                await ShowDialogAsync(errorDialog);
            }
        }
    }

    private void UpdateUpstreamOpenVpnSummary()
    {
        if (UpstreamOpenVpnSummaryText is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(currentOvpnConfigText))
        {
            UpstreamOpenVpnSummaryText.Text = "No configuration file imported.";
            return;
        }

        var (remoteHost, remotePort, proto) = UpstreamOpenVpnValidator.ParseServerInfo(currentOvpnConfigText);
        if (!string.IsNullOrEmpty(remoteHost))
        {
            UpstreamOpenVpnSummaryText.Text = $"Remote: {remoteHost}:{remotePort ?? 1194} ({(proto ?? "tcp").ToUpperInvariant()})";
        }
        else
        {
            UpstreamOpenVpnSummaryText.Text = "Configuration loaded (custom remote).";
        }
    }
}
