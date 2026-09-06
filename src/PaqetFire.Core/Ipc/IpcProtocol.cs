namespace PaqetFire.Core.Ipc;

public static class IpcProtocol
{
    public const int Version = 8;

    public const string PipeName = "PaqetFire.Broker.v8";

    public const int MaxMessageSizeBytes = 64 * 1024;
}
