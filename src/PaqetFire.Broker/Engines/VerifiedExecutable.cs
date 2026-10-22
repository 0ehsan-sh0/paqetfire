using System.Security.Cryptography;

namespace PaqetFire.Broker.Engines;

/// <summary>
/// Hashes an engine executable through a handle that denies writers and deletion, and
/// keeps that handle open until the caller has started the process. The bytes Windows
/// maps for the new process are therefore the bytes that were verified.
/// </summary>
internal static class VerifiedExecutable
{
    public static async ValueTask<FileStream> OpenAsync(
        string path,
        string expectedSha256,
        Func<Exception> mismatch,
        CancellationToken cancellationToken)
    {
        var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        try
        {
            var digest = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
            if (!CryptographicOperations.FixedTimeEquals(digest, Convert.FromHexString(expectedSha256)))
            {
                throw mismatch();
            }

            return stream;
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
