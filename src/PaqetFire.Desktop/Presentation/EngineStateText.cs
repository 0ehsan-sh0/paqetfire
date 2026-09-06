using PaqetFire.Core.Engines;

namespace PaqetFire.Desktop.Presentation;

public static class EngineStateText
{
    public static string GetDisplayName(EngineState state) => state switch
    {
        EngineState.NotInstalled => "Not Installed",
        EngineState.Stopped => "Stopped",
        EngineState.Starting => "Starting",
        EngineState.Running => "Running",
        EngineState.Stopping => "Stopping",
        EngineState.Faulted => "Needs Attention",
        _ => "Unknown",
    };
}
