namespace PaqetFire.Desktop.ViewModels;

public enum BrokerConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    Disconnecting,
    NotReady,
    Degraded,
    Faulted,
}
