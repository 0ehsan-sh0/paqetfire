using PaqetFire.Core.Engines;

namespace PaqetFire.Core.Connections;

public sealed class ConnectionTransitionException : Exception
{
    public ConnectionTransitionException(
        string operation,
        EngineKind engine,
        Exception cause,
        IReadOnlyList<Exception>? rollbackErrors = null)
        : base(CreateMessage(operation, engine, rollbackErrors), cause)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);

        Operation = operation;
        Engine = engine;
        RollbackErrors = rollbackErrors ?? [];
    }

    public string Operation { get; }

    public EngineKind Engine { get; }

    public IReadOnlyList<Exception> RollbackErrors { get; }

    private static string CreateMessage(
        string operation,
        EngineKind engine,
        IReadOnlyList<Exception>? rollbackErrors)
    {
        var rollbackDetail = rollbackErrors is { Count: > 0 }
            ? $" Rollback also reported {rollbackErrors.Count} error(s)."
            : string.Empty;

        return $"Connection operation '{operation}' failed at the {engine} engine.{rollbackDetail}";
    }
}
