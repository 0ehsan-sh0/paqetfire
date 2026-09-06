using PaqetFire.Core.Ipc;

namespace PaqetFire.Broker.Ipc;

public interface IBrokerRequestHandler
{
    ValueTask<BrokerResponse> HandleAsync(
        BrokerRequest request,
        CancellationToken cancellationToken);
}
