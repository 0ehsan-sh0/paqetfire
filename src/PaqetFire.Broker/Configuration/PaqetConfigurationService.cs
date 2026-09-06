using PaqetFire.Core.Configuration;

namespace PaqetFire.Broker.Configuration;

/// <summary>
/// The only broker component that turns user input into the Paqet client YAML.
/// The supplied store constrains the write to its exact broker-owned path and
/// performs an atomic replace with a last-known-good backup.
/// </summary>
public sealed class PaqetConfigurationService
{
    private readonly IPaqetConfigurationWriter _writer;
    private readonly IAtomicConfigurationStore _store;
    private readonly string _configurationPath;

    public PaqetConfigurationService(
        IPaqetConfigurationWriter writer,
        IAtomicConfigurationStore store,
        string configurationPath)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(configurationPath);
        if (!Path.IsPathFullyQualified(configurationPath))
        {
            throw new ArgumentException("The Paqet configuration path must be absolute.", nameof(configurationPath));
        }

        _writer = writer;
        _store = store;
        _configurationPath = Path.GetFullPath(configurationPath);
    }

    public string ConfigurationPath => _configurationPath;

    public Task SaveAsync(
        PaqetProfile profile,
        string transportKey,
        CancellationToken cancellationToken = default)
    {
        var validatedYaml = _writer.Write(profile, transportKey);
        return _store.WriteAsync(_configurationPath, validatedYaml, cancellationToken);
    }

    public Task RollbackAsync(CancellationToken cancellationToken = default) =>
        _store.RollbackAsync(_configurationPath, cancellationToken);
}
