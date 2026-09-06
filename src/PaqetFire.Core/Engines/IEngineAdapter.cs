namespace PaqetFire.Core.Engines;

public interface IEngineAdapter
{
    EngineKind Kind { get; }

    ValueTask<EngineStatus> GetStatusAsync(CancellationToken cancellationToken);

    ValueTask StartAsync(CancellationToken cancellationToken);

    ValueTask StopAsync(CancellationToken cancellationToken);
}
