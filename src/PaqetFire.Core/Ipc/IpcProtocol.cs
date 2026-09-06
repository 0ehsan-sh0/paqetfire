namespace PaqetFire.Core.Ipc;

public static class IpcProtocol
{
    public const int Version = 7;

    public const string PipeName = "PaqetFire.Broker.v7";

    public const int MaxMessageSizeBytes = 64 * 1024;
}
