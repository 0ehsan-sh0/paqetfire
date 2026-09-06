namespace PaqetFire.Core.Ipc;

public static class IpcProtocol
{
    public const int Version = 6;

    public const string PipeName = "PaqetFire.Broker.v6";

    public const int MaxMessageSizeBytes = 64 * 1024;
}
