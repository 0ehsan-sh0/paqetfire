using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PaqetFire.Core.Deployment;
using PaqetFire.Core.Ipc;
using PaqetFire.Desktop.Presentation;
using PaqetFire.Desktop.Services;

namespace PaqetFire.Desktop;

/// <summary>Required software and the first-run checklist.</summary>
public sealed partial class MainWindow
{
    private static readonly TimeSpan PrerequisiteRecheckInterval = TimeSpan.FromSeconds(3);

    private DateTimeOffset lastPrerequisiteRecheck = DateTimeOffset.MinValue;

    private async void PrerequisiteActionButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string id })
        {
            await InstallPrerequisiteAsync(id, PrerequisitesInfoBar);
        }
    }

    private async void InstallAllMissingButton_Click(object sender, RoutedEventArgs e)
    {
        var missing = prerequisites.Values.Where(item => !item.IsInstalled).Select(item => item.Id).ToArray();
        if (missing.Length == 0 || prerequisiteActionInProgress)
        {
            return;
        }

        // Automatic installers first, one at a time (each shows its own permission prompt);
        // download pages last so they do not hide an installer that is waiting.
        foreach (var id in missing.OrderBy(id => PrerequisiteInstallerService.CanInstallAutomatically(id) ? 0 : 1))
        {
            if (!await InstallPrerequisiteAsync(id, OverviewInfoBar))
            {
                break;
            }
        }
    }

    /// <returns>False when the user cancelled or an install failed, so a batch should stop.</returns>
    private async Task<bool> InstallPrerequisiteAsync(string id, InfoBar feedback)
    {
        if (prerequisiteActionInProgress || !prerequisites.TryGetValue(id, out var prerequisite) || prerequisite.IsInstalled)
        {
            return true;
        }

        if (!PrerequisiteInstallerService.CanInstallAutomatically(id))
        {
            if (prerequisite.HelpUri is null)
            {
                ShowInfo(feedback, InfoBarSeverity.Warning, "Download unavailable", "No official download page was reported for this component.");
                return true;
            }

            try
            {
                PrerequisiteInstallerService.OpenOfficialPage(prerequisite.HelpUri);
                ShowInfo(
                    feedback,
                    InfoBarSeverity.Informational,
                    $"Install {prerequisite.DisplayName}",
                    id == "npcap"
                        ? "The official Npcap page is open. Install Npcap with \"WinPcap API-compatible mode\" enabled, then return here; PaqetFire checks again automatically."
                        : "The official Microsoft download page is open. Finish the installer, then return here; PaqetFire checks again automatically.");
                AddActivity($"Opened the official {prerequisite.DisplayName} download page.");
            }
            catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
            {
                ShowInfo(feedback, InfoBarSeverity.Error, "Could not open the download page", exception.Message);
                return false;
            }

            return true;
        }

        prerequisiteActionInProgress = true;
        UpdatePrerequisiteCards();
        try
        {
            var progress = new Progress<string>(message =>
                ShowInfo(feedback, InfoBarSeverity.Informational, "Installing required software", message));
            var result = await prerequisiteInstaller.InstallAsync(id, progress);
            ShowInfo(
                feedback,
                result.RestartRequired ? InfoBarSeverity.Warning : InfoBarSeverity.Success,
                result.RestartRequired ? "Installation complete — restart required" : "Installation complete",
                result.RestartRequired
                    ? "Restart Windows before connecting with PaqetFire."
                    : "PaqetFire is checking the component again now.");
            AddActivity($"Installed {prerequisite.DisplayName}.");
            return true;
        }
        catch (OperationCanceledException exception)
        {
            ShowInfo(feedback, InfoBarSeverity.Warning, "Installation cancelled", exception.Message);
            AddActivity($"Installation cancelled for {prerequisite.DisplayName}.");
            return false;
        }
        catch (Exception exception) when (
            exception is HttpRequestException or IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException)
        {
            ShowInfo(feedback, InfoBarSeverity.Error, "Installation failed", exception.Message);
            AddActivity($"Could not install {prerequisite.DisplayName}.", "ERROR");
            return false;
        }
        finally
        {
            prerequisiteActionInProgress = false;
            UpdatePrerequisiteCards();
            // Re-check whenever an installer process has exited, whatever its result.
            lastPrerequisiteRecheck = DateTimeOffset.UtcNow;
            await ViewModel.RefreshAsync();
        }
    }

    /// <summary>Re-checks required software when the user comes back from an installer or download page.</summary>
    private void OnWindowActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated || !initialized ||
            prerequisiteActionInProgress || ViewModel.IsBusy ||
            !prerequisites.Values.Any(item => !item.IsInstalled) ||
            DateTimeOffset.UtcNow - lastPrerequisiteRecheck < PrerequisiteRecheckInterval)
        {
            return;
        }

        lastPrerequisiteRecheck = DateTimeOffset.UtcNow;
        _ = ViewModel.RefreshAsync();
    }

    private void UpdatePrerequisites(BrokerSnapshot snapshot, BrokerSnapshot? previousSnapshot)
    {
        var hadMissing = prerequisites.Values.Any(item => !item.IsInstalled);
        prerequisites.Clear();
        if (snapshot.Prerequisites is { Count: > 0 } reported)
        {
            foreach (var prerequisite in reported)
            {
                prerequisites[prerequisite.Id] = prerequisite;
            }

            var missing = reported.Count(item => !item.IsInstalled);
            PrerequisiteSummaryText.Text = missing == 0
                ? "Prerequisites · Ready"
                : $"Prerequisites · {missing} missing";
        }
        else
        {
            PrerequisiteSummaryText.Text = "Prerequisites · No report";
        }

        UpdatePrerequisiteCards();
        UpdateSetupChecklist(snapshot);

        // Everything is installed now: the next step is the server profile.
        if (hadMissing && previousSnapshot is not null && snapshot.Prerequisites is { Count: > 0 } &&
            !prerequisites.Values.Any(item => !item.IsInstalled) && !snapshot.IsConfigured && AppWindow.IsVisible)
        {
            ShowInfo(OverviewInfoBar, InfoBarSeverity.Success, "Required software is ready",
                "Next, enter your Paqet server endpoint and transport key.");
            FocusServerEndpoint();
        }
    }

    private void UpdateSetupChecklist(BrokerSnapshot snapshot)
    {
        var checklist = SetupChecklist.Create(snapshot, ViewModel.IsConnected);
        SetupChecklistCard.Visibility = checklist.ShouldShow ? Visibility.Visible : Visibility.Collapsed;
        // Snapshots repeat every few seconds; rebuilding would move focus and re-announce the list.
        if (!ChecklistEntries.SequenceEqual(checklist.Items))
        {
            ChecklistEntries.Clear();
            foreach (var item in checklist.Items)
            {
                ChecklistEntries.Add(item);
            }
        }

        InstallAllMissingButton.Visibility = checklist.MissingPrerequisites.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        InstallAllMissingButton.IsEnabled = !prerequisiteActionInProgress;
        ChecklistServerButton.Visibility = checklist.CurrentStep == SetupStep.ServerProfile ? Visibility.Visible : Visibility.Collapsed;
        var manualOnly = checklist.MissingPrerequisites.Where(item => !PrerequisiteInstallerService.CanInstallAutomatically(item.Id)).ToArray();
        ChecklistNoteText.Text = manualOnly.Length == 0
            ? string.Empty
            : $"{string.Join(" and ", manualOnly.Select(item => item.DisplayName))} must be installed from the official download page" +
              (manualOnly.Any(item => item.Id == "npcap") ? "; for Npcap, enable \"WinPcap API-compatible mode\" in its installer." : ".");
        ChecklistNoteText.Visibility = manualOnly.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ChecklistServerButton_Click(object sender, RoutedEventArgs e) => FocusServerEndpoint();

    /// <summary>Deep link from a prerequisite error to the Required software card.</summary>
    private void ShowRequiredSoftware()
    {
        NavigateTo("diagnostics");
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            RequiredSoftwareCard.StartBringIntoView(new BringIntoViewOptions { VerticalAlignmentRatio = 0, AnimationDesired = true }));
    }

    private void UpdatePrerequisiteCards()
    {
        UpdatePrerequisiteCard(
            "npcap",
            NpcapPrerequisiteStatusText,
            NpcapPrerequisiteDetailText,
            NpcapPrerequisiteButton,
            "Open official download");
        UpdatePrerequisiteCard(
            "winpkfilter",
            WinpkFilterPrerequisiteStatusText,
            WinpkFilterPrerequisiteDetailText,
            WinpkFilterPrerequisiteButton,
            "Download and install");
        UpdatePrerequisiteCard(
            "vcredist-x64",
            VisualCppPrerequisiteStatusText,
            VisualCppPrerequisiteDetailText,
            VisualCppPrerequisiteButton,
            "Download and install");
        UpdatePrerequisiteCard(
            "dotnet-framework",
            DotNetPrerequisiteStatusText,
            DotNetPrerequisiteDetailText,
            DotNetPrerequisiteButton,
            "Open Microsoft download");
        if (InstallAllMissingButton is not null)
        {
            InstallAllMissingButton.IsEnabled = !prerequisiteActionInProgress;
        }
    }

    private void UpdatePrerequisiteCard(
        string id,
        TextBlock statusText,
        TextBlock detailText,
        Button actionButton,
        string actionLabel)
    {
        if (!prerequisites.TryGetValue(id, out var prerequisite))
        {
            statusText.Text = "Not checked";
            actionButton.IsEnabled = false;
            return;
        }

        statusText.Text = prerequisite.IsInstalled ? "✓ Installed" : "⚠ Missing";
        detailText.Text = prerequisite.Detail;
        actionButton.Content = prerequisite.IsInstalled ? "Installed" : actionLabel;
        actionButton.IsEnabled = !prerequisite.IsInstalled && !prerequisiteActionInProgress;
    }
}
