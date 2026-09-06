using PaqetFire.Core.Engines;

namespace PaqetFire.Broker.Engines;

internal sealed class UnavailableEngineAdapter(EngineKind kind) : IEngineAdapter
{
    public EngineKind Kind { get; } = kind;

    public ValueTask<EngineStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new EngineStatus(
            Kind,
            EngineState.NotInstalled,
            Detail: "The bundled engine payload has not been staged."));
    }

    public ValueTask StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        throw new InvalidOperationException($"The bundled {Kind} engine is unavailable.");
    }

    public ValueTask StopAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }
}
