using PaqetFire.Core.Ipc;
using CoreConnectionState = PaqetFire.Core.Connections.ConnectionState;

namespace PaqetFire.Desktop.Presentation;

/// <summary>
/// The broker makes a new profile active only while the route is disconnected: it has no
/// server or key yet, so selecting it would restart a running route with it.
/// </summary>
public static class ProfileCreation
{
    public static bool BecomesActive(BrokerSnapshot? snapshot) =>
        snapshot is null || snapshot.ConnectionState == CoreConnectionState.Disconnected;

    public static string DialogHint(bool becomesActive, bool hasPendingChanges, string? activeProfileName) =>
        !becomesActive
            ? $"The new profile starts with default settings. The route stays connected with '{activeProfileName ?? "the current profile"}'; disconnect, then select the new profile to set it up."
            : hasPendingChanges
                ? "The new profile starts with default settings and becomes active. Unsaved changes on this page are discarded."
                : "The new profile starts with default settings and becomes active. Enter its server and transport key, then save.";

    public static string AddedWhileConnected(string name, string? activeProfileName) =>
        $"Profile '{name}' was added. The route keeps using '{activeProfileName ?? "the current profile"}'; disconnect, then select '{name}' to enter its server and transport key.";
}
