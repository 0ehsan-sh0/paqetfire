using PaqetFire.Core.Configuration;
using PaqetFire.Core.Routing;
using PaqetFire.Desktop.Presentation;
using Xunit;

namespace PaqetFire.Broker.Tests;

public sealed class RoutingScopeTextTests
{
    [Theory]
    [InlineData(0, "No apps selected")]
    [InlineData(1, "1 selected app")]
    [InlineData(3, "3 selected apps")]
    public void SelectedModeReportsEffectiveApplicationCount(int count, string expected)
    {
        var settings = new PaqetFireSettings
        {
            RoutingMode = RoutingMode.SelectedApplications,
            SelectedApplications = Enumerable.Range(0, count).Select(index => $"app{index}.exe").ToArray(),
        };
        Assert.Equal(expected, RoutingScopeText.Create(PaqetFireSettingsView.FromSettings(settings)));
    }

    [Fact]
    public void AllModeDoesNotUseRetainedSelectedApplicationList()
    {
        var settings = new PaqetFireSettings
        {
            RoutingMode = RoutingMode.AllApplications,
            SelectedApplications = ["retained.exe"],
        };
        Assert.Equal("All supported apps", RoutingScopeText.Create(PaqetFireSettingsView.FromSettings(settings)));
    }

    [Fact]
    public void MissingSettingsDoNotClaimEverythingIsRouted() =>
        Assert.Equal("Application scope unavailable", RoutingScopeText.Create(null));
}
