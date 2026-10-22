using System.Collections.Concurrent;
using System.Security.Cryptography;
using PaqetFire.Broker.Configuration;
using PaqetFire.Broker.Deployment;

namespace PaqetFire.Broker.RoutingData;

public sealed record RoutingDataFile(string Name, long Size, string Sha256);

/// <summary>The GeoIP/GeoSite files one Xray start loads.</summary>
/// <param name="AssetDirectory">The directory Xray is pointed at, or null for the files bundled beside it.</param>
public sealed record RoutingDataSet(string? AssetDirectory, string Version, IReadOnlyList<RoutingDataFile> Files)
{
    public static RoutingDataSet Bundled { get; } = new(null, "bundled", []);

    public bool IsUpdated => AssetDirectory is not null;

    // A downloaded set is promoted only after Xray accepted every category PaqetFire uses.
    public bool SupportsMalwareCategories => IsUpdated;

    /// <summary>Identifies the data, so a configuration validated against one set is not assumed valid with another.</summary>
    public string Fingerprint => IsUpdated
        ? $"{Version}:{string.Join(",", Files.Select(file => file.Sha256))}"
        : "bundled";

    public bool IsSameData(RoutingDataSet? other) =>
        other is not null &&
        string.Equals(AssetDirectory, other.AssetDirectory, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(Fingerprint, other.Fingerprint, StringComparison.Ordinal);
}

/// <summary>
/// Xray could not start with downloaded routing data: the files could not be verified or
/// loading them took too long. The start is retried with the bundled data.
/// </summary>
public sealed class RoutingDataUnavailableException(string message, Exception inner) : Exception(message, inner);

/// <summary>Handles that keep verified routing data files from being replaced until disposed.</summary>
public sealed class RoutingDataLease : IAsyncDisposable
{
    private readonly IReadOnlyList<FileStream> handles;

    internal RoutingDataLease(IReadOnlyList<FileStream> handles) => this.handles = handles;

    public static RoutingDataLease None { get; } = new([]);

    public async ValueTask DisposeAsync()
    {
        foreach (var handle in handles)
        {
            await handle.DisposeAsync().ConfigureAwait(false);
        }
    }
}

/// <summary>
/// Checks downloaded routing data against the sizes and digests recorded when it was
/// installed. Each file is opened with a handle that denies writers and deletion, and a
/// file whose identity is unchanged since it last passed is not hashed again.
/// </summary>
public sealed class RoutingDataFileVerifier(ProtectedDirectoryPolicy? trust = null)
{
    private readonly ProtectedDirectoryPolicy trust = trust ?? ProtectedDirectoryPolicy.BrokerPrivate;
    private readonly ConcurrentDictionary<string, (FileIdentity Identity, string Sha256)> verified =
        new(StringComparer.OrdinalIgnoreCase);

    /// <exception cref="InvalidDataException">
    /// The data is not what was installed: a size or digest differs, a file or the directory
    /// is missing, is a link, has more than one name, or has an owner nobody trusted.
    /// </exception>
    /// <exception cref="IOException">The files could not be read just now, for example while another program holds them.</exception>
    public async ValueTask<RoutingDataLease> OpenAsync(RoutingDataSet set, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(set);
        if (set.AssetDirectory is not { } directory)
        {
            return RoutingDataLease.None;
        }

        var handles = new List<FileStream>(set.Files.Count);
        try
        {
            var folder = InspectOrMissing(directory);
            if (folder.IsReparsePoint || !folder.IsDirectory || !trust.IsTrusted(folder.Owner))
            {
                throw new InvalidDataException("The downloaded routing data directory is not one PaqetFire created.");
            }

            foreach (var file in set.Files)
            {
                var path = Path.Combine(directory, file.Name);
                FileStream stream;
                try
                {
                    // Opened without following links; writers are denied until the lease ends.
                    stream = SecureFileSystem.OpenTrustedFileForRead(path, trust);
                }
                catch (FileNotFoundException exception)
                {
                    throw new InvalidDataException($"The downloaded routing data file {file.Name} is missing.", exception);
                }

                handles.Add(stream);
                if (stream.Length != file.Size)
                {
                    throw Mismatch(file);
                }

                var identity = FileIdentity.TryRead(stream.SafeFileHandle);
                if (identity is not null &&
                    verified.TryGetValue(path, out var cached) &&
                    cached.Identity == identity &&
                    string.Equals(cached.Sha256, file.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                verified.TryRemove(path, out _);
                var digest = Convert.ToHexStringLower(
                    await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
                if (!string.Equals(digest, file.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw Mismatch(file);
                }

                // The handle denies writers, so the identity read through it describes the hashed bytes.
                if (identity is not null)
                {
                    verified[path] = (identity, file.Sha256);
                }
            }

            return new RoutingDataLease(handles);
        }
        catch
        {
            foreach (var handle in handles)
            {
                await handle.DisposeAsync().ConfigureAwait(false);
            }

            throw;
        }
    }

    private static SecureEntry InspectOrMissing(string path)
    {
        try
        {
            return SecureFileSystem.Inspect(path);
        }
        catch (FileNotFoundException exception)
        {
            throw new InvalidDataException("The downloaded routing data directory is missing.", exception);
        }
    }

    private static InvalidDataException Mismatch(RoutingDataFile file) =>
        new($"The downloaded routing data file {file.Name} failed its integrity check.");
}
