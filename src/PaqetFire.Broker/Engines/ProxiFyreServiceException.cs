namespace PaqetFire.Broker.Engines;

public sealed class ProxiFyreServiceException : InvalidOperationException
{
    internal ProxiFyreServiceException(string operation, string message)
        : base(message)
    {
        Operation = operation;
    }

    public string Operation { get; }
}
