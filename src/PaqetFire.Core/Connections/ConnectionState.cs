namespace PaqetFire.Core.Connections;

public enum ConnectionState
{
    NotReady,
    Disconnected,
    Guarded,
    Connecting,
    Connected,
    Disconnecting,
    Degraded,
    Faulted,
}
