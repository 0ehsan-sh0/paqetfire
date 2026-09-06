namespace PaqetFire.Desktop.ViewModels;

public enum BrokerConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    Disconnecting,
    Guarded,
    NotReady,
    Degraded,
    Faulted,
}
