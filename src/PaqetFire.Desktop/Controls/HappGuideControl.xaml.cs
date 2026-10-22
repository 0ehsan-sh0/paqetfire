using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PaqetFire.Desktop.Services;
using Windows.ApplicationModel.DataTransfer;

namespace PaqetFire.Desktop.Controls;

public sealed partial class HappGuideControl : UserControl
{
    private int qrVersion;

    public event EventHandler? BackRequested;

    /// <summary>The saved sharing password cannot be shown; ask the window to generate a new one.</summary>
    public event EventHandler? RotateCredentialsRequested;

    public HappGuideControl()
    {
        InitializeComponent();
    }

    public void UpdateEndpoint(string address, int port, string username, string password, string socksUri)
    {
        ServerAddressBox.Text = address;
        ServerPortBox.Text = port > 0 ? port.ToString() : "10808";
        UsernameBox.Text = username;
        PasswordBox.Text = password;
        SocksUriBox.Text = socksUri;
        PasswordBox.PlaceholderText = "Saved password is protected; rotate credentials to show a new one";
        CopyPasswordButton.IsEnabled = !string.IsNullOrEmpty(password);
        CopyUriButton.IsEnabled = !string.IsNullOrEmpty(socksUri);
        QrPlaceholderText.Text = string.IsNullOrEmpty(address)
            ? "Detect the hotspot on the Routing page to show a QR code."
            : string.IsNullOrEmpty(password)
                ? "The saved sharing password is protected and cannot be shown. Rotate credentials to show a QR code, then save on the Routing page."
                : "Preparing the QR code…";
        RotateCredentialsButton.Visibility = !string.IsNullOrEmpty(address) && string.IsNullOrEmpty(password)
            ? Visibility.Visible
            : Visibility.Collapsed;
        _ = RenderQrAsync(socksUri);
    }

    private async Task RenderQrAsync(string socksUri)
    {
        var version = ++qrVersion;
        var image = await QrCodeImage.CreateAsync(socksUri);
        if (version != qrVersion)
        {
            return;
        }

        QrImage.Source = image;
        QrImage.Visibility = image is null ? Visibility.Collapsed : Visibility.Visible;
        QrPlaceholder.Visibility = image is null ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RotateCredentialsButton_Click(object sender, RoutedEventArgs e) =>
        RotateCredentialsRequested?.Invoke(this, EventArgs.Empty);

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        BackRequested?.Invoke(this, EventArgs.Empty);
    }

    private void CopySocksUriButton_Click(object sender, RoutedEventArgs e)
    {
        CopyToClipboard(SocksUriBox.Text, "SOCKS URI");
    }

    private void CopyServerAddressButton_Click(object sender, RoutedEventArgs e)
    {
        CopyToClipboard(ServerAddressBox.Text, "Server address");
    }

    private void CopyServerPortButton_Click(object sender, RoutedEventArgs e)
    {
        CopyToClipboard(ServerPortBox.Text, "Port");
    }

    private void CopyUsernameButton_Click(object sender, RoutedEventArgs e)
    {
        CopyToClipboard(UsernameBox.Text, "Username");
    }

    private void CopyPasswordButton_Click(object sender, RoutedEventArgs e)
    {
        CopyToClipboard(PasswordBox.Text, "Password");
    }

    private void CopyToClipboard(string text, string label)
    {
        if (string.IsNullOrEmpty(text))
        {
            ShowFeedback(InfoBarSeverity.Warning, "Nothing to copy", $"{label} is not set yet. Detect your hotspot first.");
            return;
        }

        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);
        ShowFeedback(InfoBarSeverity.Success, "Copied", $"{label} copied to the clipboard.");
    }

    private void ShowFeedback(InfoBarSeverity severity, string title, string message)
    {
        FeedbackInfoBar.Severity = severity;
        FeedbackInfoBar.Title = title;
        FeedbackInfoBar.Message = message;
        FeedbackInfoBar.IsOpen = true;
    }
}
