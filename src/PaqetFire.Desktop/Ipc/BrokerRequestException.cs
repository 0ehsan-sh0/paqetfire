using PaqetFire.Core.Ipc;

namespace PaqetFire.Desktop.Ipc;

public sealed class BrokerRequestException(BrokerError error) : Exception(error.Message)
{
    public BrokerError Error { get; } = error;
}
