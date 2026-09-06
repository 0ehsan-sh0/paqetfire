using System.Net;
using PaqetFire.Broker.Configuration;
using PaqetFire.Core.Deployment;
using PaqetFire.Core.Engines;

namespace PaqetFire.Broker.Engines;

public sealed class PaqetEngineFactory
{
    public PaqetProcessAdapter Create(
        PaqetRuntimePaths paths,
        BundledEngine manifestEntry,
        IPEndPoint socksEndpoint)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(manifestEntry);
        ArgumentNullException.ThrowIfNull(socksEndpoint);
        if (manifestEntry.Engine != EngineKind.Paqet)
        {
            throw new ArgumentException("The payload manifest entry is not Paqet.", nameof(manifestEntry));
        }

        var entryPoint = manifestEntry.Files.SingleOrDefault(file =>
            string.Equals(file.Path, manifestEntry.EntryPoint, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException("The Paqet entry point is not in the payload inventory.");
        var executablePath = Path.GetFullPath(Path.Combine(paths.PayloadRoot, manifestEntry.EntryPoint));
        if (!string.Equals(executablePath, Path.GetFullPath(paths.ExecutablePath), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The Paqet manifest entry point does not match the runtime layout.");
        }

        return new PaqetProcessAdapter(new PaqetProcessOptions
        {
            ExecutablePath = executablePath,
            ConfigurationPath = paths.ConfigurationPath,
            TrustedExecutableRoot = paths.PayloadRoot,
            TrustedConfigurationRoot = paths.ConfigurationRoot,
            SocksEndpoint = socksEndpoint,
            Version = manifestEntry.Version,
            ExpectedExecutableSha256 = entryPoint.Sha256,
        });
    }
}
