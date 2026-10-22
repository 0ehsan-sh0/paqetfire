using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using PaqetFire.Core.Deployment;

namespace PaqetFire.Broker.Deployment;

/// <summary>
/// Verifies every file the payload manifest declares. The first inspection hashes all of
/// them in parallel; later ones re-hash only files whose identity (volume, file ID, size,
/// last write) changed. Engine executables are additionally re-hashed by their adapters
/// immediately before each launch, through a handle that blocks replacement until then.
/// </summary>
public sealed class PayloadIntegrityInspector
{
    private const string ManifestFileName = "payload-manifest.json";
    private readonly string _payloadRoot;
    private readonly ConcurrentDictionary<string, VerifiedFile> _verified = new(StringComparer.OrdinalIgnoreCase);

    public PayloadIntegrityInspector()
        : this(Path.Combine(AppContext.BaseDirectory, "payload"))
    {
    }

    internal PayloadIntegrityInspector(string payloadRoot)
    {
        _payloadRoot = Path.GetFullPath(payloadRoot);
    }

    public async ValueTask<PayloadInspection> InspectAsync(
        CancellationToken cancellationToken)
    {
        var manifestPath = Path.Combine(_payloadRoot, ManifestFileName);
        if (!File.Exists(manifestPath))
        {
            return new PayloadInspection(
                PayloadInspectionState.NotStaged,
                "The bundled engine payload has not been staged for this build.");
        }

        try
        {
            var manifest = ProductPayloadManifest.Load(manifestPath);
            ValidateManifest(manifest);
            foreach (var engine in manifest.Engines)
            {
                ValidateEngineEntryPoint(engine);
            }

            await Parallel.ForEachAsync(
                    manifest.Engines.SelectMany(engine => engine.Files),
                    new ParallelOptions
                    {
                        CancellationToken = cancellationToken,
                        MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 1, 4),
                    },
                    VerifyAsync)
                .ConfigureAwait(false);

            var versions = manifest.Engines.ToDictionary(
                engine => engine.Engine.ToString(),
                engine => engine.Version,
                StringComparer.OrdinalIgnoreCase);

            return new PayloadInspection(
                PayloadInspectionState.Ready,
                "Every bundled engine file passed integrity verification.",
                versions);
        }
        catch (Exception exception) when (
            exception is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return new PayloadInspection(
                PayloadInspectionState.Invalid,
                exception.Message);
        }
    }

    private async ValueTask VerifyAsync(PayloadFile file, CancellationToken cancellationToken)
    {
        var path = Path.GetFullPath(Path.Combine(_payloadRoot, file.Path));
        var identity = FileIdentity.TryRead(path);
        if (identity is not null &&
            _verified.TryGetValue(path, out var cached) &&
            cached.Identity == identity &&
            string.Equals(cached.Sha256, file.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _verified.TryRemove(path, out _);
        await PayloadVerifier.VerifyAsync(_payloadRoot, file, cancellationToken).ConfigureAwait(false);
        // Re-read after hashing: a file replaced during the hash must not be cached as verified.
        if (identity is not null && FileIdentity.TryRead(path) == identity)
        {
            _verified[path] = new VerifiedFile(identity, file.Sha256);
        }
    }

    private static void ValidateManifest(ProductPayloadManifest manifest)
    {
        if (!string.Equals(manifest.SchemaVersion, "1", StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Unsupported payload manifest schema '{manifest.SchemaVersion}'.");
        }

        var expectedArchitecture = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            Architecture.X86 => "x86",
            _ => throw new InvalidDataException("This Windows architecture is unsupported."),
        };

        if (!string.Equals(
                manifest.Architecture,
                expectedArchitecture,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Payload architecture '{manifest.Architecture}' does not match " +
                $"the broker architecture '{expectedArchitecture}'.");
        }

        if (manifest.Engines.Count == 0)
        {
            throw new InvalidDataException("The payload manifest contains no engines.");
        }

        var duplicateEngine = manifest.Engines
            .GroupBy(engine => engine.Engine)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateEngine is not null)
        {
            throw new InvalidDataException(
                $"The payload declares engine '{duplicateEngine.Key}' more than once.");
        }
    }

    private static void ValidateEngineEntryPoint(BundledEngine engine)
    {
        if (string.IsNullOrWhiteSpace(engine.Version))
        {
            throw new InvalidDataException($"Engine '{engine.Engine}' has no version.");
        }

        var entryPoint = engine.Files.FirstOrDefault(file =>
            string.Equals(file.Path, engine.EntryPoint, StringComparison.OrdinalIgnoreCase));

        if (entryPoint is null)
        {
            throw new InvalidDataException(
                $"Engine '{engine.Engine}' entry point is not declared in its file inventory.");
        }
    }

    private sealed record VerifiedFile(FileIdentity Identity, string Sha256);
}
