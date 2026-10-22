using PaqetFire.Core.Configuration;
using PaqetFire.Core.Connections;
using PaqetFire.Core.Ipc;
using PaqetFire.Core.RoutingData;
using PaqetFire.Desktop.Presentation;
using PaqetFire.Desktop.ViewModels;
using Xunit;

namespace PaqetFire.Broker.Tests;

public sealed class DesktopRoutingDataPresentationTests
{
    private static readonly RoutingDataStatus Bundled =
        new(RoutingDataOrigin.Bundled, "Xray 26.3.27", null, null, true, false, false);

    private static readonly RoutingDataStatus Updated = new(
        RoutingDataOrigin.Updated, "202610220935", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, true, false, true);

    [Fact]
    public void TheSourceNamesTheReleaseOrTheBundledXray()
    {
        Assert.Equal("Iran-v2ray-rules 2026-10-22 09:35 UTC", RoutingDataText.Source(Updated));
        Assert.Equal("Bundled with Xray 26.3.27", RoutingDataText.Source(Bundled));
        Assert.Equal("Waiting for the broker…", RoutingDataText.Source(null));
        Assert.StartsWith("Downloading", RoutingDataText.Checked(Bundled with { IsUpdating = true }), StringComparison.Ordinal);
        Assert.Equal("Not checked yet", RoutingDataText.Checked(Bundled));
    }

    [Fact]
    public void AnUpdateTheUserStartedReportsItsOutcome()
    {
        Assert.Equal((true, "Routing data updated"),
            Outcome(RoutingDataText.UpdateResult("Xray 26.3.27", Updated)));
        Assert.Equal((true, "Routing data is up to date"),
            Outcome(RoutingDataText.UpdateResult("202610220935", Updated)));
        var failed = RoutingDataText.UpdateResult("Xray 26.3.27", Bundled with { LastError = "GitHub could not be reached." });
        Assert.False(failed.Succeeded);
        Assert.Equal("GitHub could not be reached.", failed.Message);

        static (bool, string) Outcome((bool Succeeded, string Title, string Message) result) => (result.Succeeded, result.Title);
    }

    [Fact]
    public void ANewProfileBecomesActiveOnlyWhileTheRouteIsIdle()
    {
        var idle = new BrokerSnapshot([], false, false, DateTimeOffset.UtcNow, ConnectionState: ConnectionState.Disconnected);
        Assert.True(ProfileCreation.BecomesActive(idle));
        Assert.True(ProfileCreation.BecomesActive(null));
        Assert.False(ProfileCreation.BecomesActive(idle with { ConnectionState = ConnectionState.Connected }));
        Assert.False(ProfileCreation.BecomesActive(idle with { ConnectionState = ConnectionState.Guarded }));
        // Transport recovery is reported as Connecting.
        Assert.False(ProfileCreation.BecomesActive(idle with { ConnectionState = ConnectionState.Connecting }));
        Assert.Contains("Office", ProfileCreation.AddedWhileConnected("Travel", "Office"), StringComparison.Ordinal);
        Assert.Contains("stays connected", ProfileCreation.DialogHint(false, false, "Office"), StringComparison.Ordinal);
        Assert.Contains("becomes active", ProfileCreation.DialogHint(true, false, "Office"), StringComparison.Ordinal);
    }

    [Fact]
    public void TheAutomaticInterfaceFollowsTheWindowsRoute()
    {
        Assert.Contains("Windows route", NetworkAdapterPreview.AutomaticOptionLabel, StringComparison.Ordinal);
        Assert.DoesNotContain("Ethernet, then Wi-Fi", NetworkAdapterPreview.AutomaticOptionLabel, StringComparison.Ordinal);
    }

    [Fact]
    public void MalwareBlockingRoundTripsThroughTheForm()
    {
        var settings = new PaqetFireSettings { BlockMalware = false };
        var form = ConnectionFormState.FromSettings(PaqetFireSettingsView.FromSettings(settings));
        Assert.False(form.BlockMalware);
        Assert.False(form.ToSettings("key", "password").BlockMalware);
        Assert.True(new ConnectionFormState().BlockMalware);
        Assert.True(new DesktopPreferences().BlockMalware);

        var preferences = new DesktopPreferences();
        form.ApplyTo(preferences);
        Assert.False(ConnectionFormState.FromPreferences(preferences).BlockMalware);
    }

    [Fact]
    public void TransportRecoveryAttemptsDoNotReportADroppedRoute()
    {
        var now = DateTimeOffset.UtcNow;
        var connected = new BrokerSnapshot([], true, false, now, IsConfigured: true, ConnectionState: ConnectionState.Connected);
        var recovering = connected with
        {
            IsRouting = false,
            ConnectionState = ConnectionState.Connecting,
            Progress = new ConnectionProgress(ConnectionStage.RecoveringTransport, now, now),
        };
        var probing = connected with { Progress = new ConnectionProgress(ConnectionStage.ProbingTunnel, now, now) };
        var nextAttempt = recovering with { Progress = recovering.Progress! with { Attempt = 2 } };
        var gaveUp = connected with { IsRouting = false, ConnectionState = ConnectionState.Disconnected };

        Assert.Equal(RouteNotificationKind.Reconnecting, RouteNotificationPolicy.Classify(connected, recovering, false)?.Kind);
        Assert.Null(RouteNotificationPolicy.Classify(recovering, probing, false));
        Assert.Null(RouteNotificationPolicy.Classify(probing, nextAttempt, false));
        Assert.Null(RouteNotificationPolicy.Classify(recovering, nextAttempt, false));
        Assert.Equal(RouteNotificationKind.RouteDropped, RouteNotificationPolicy.Classify(nextAttempt, gaveUp, false)?.Kind);
    }
}
