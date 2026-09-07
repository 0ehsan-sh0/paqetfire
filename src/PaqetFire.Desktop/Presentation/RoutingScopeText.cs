using PaqetFire.Core.Configuration;
using PaqetFire.Core.Routing;

namespace PaqetFire.Desktop.Presentation;

public static class RoutingScopeText
{
    public static string Create(PaqetFireSettingsView? settings) => settings switch
    {
        null => "Application scope unavailable",
        { RoutingMode: RoutingMode.AllApplications } => "All supported apps",
        _ => settings.SelectedApplications.Count switch
        {
            0 => "No apps selected",
            1 => "1 selected app",
            var count => $"{count} selected apps",
        },
    };
}
