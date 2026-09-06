using System.Security.Cryptography;

namespace PaqetFire.Core.Deployment;

public static class PayloadVerifier
{
    public static async ValueTask VerifyAsync(
        string payloadRoot,
        PayloadFile file,
        CancellationToken cancellationToken)
    {
        var resolvedRoot = Path.GetFullPath(payloadRoot)
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var candidate = Path.GetFullPath(Path.Combine(resolvedRoot, file.Path));

        if (!candidate.StartsWith(resolvedRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Payload path escapes the protected root: '{file.Path}'.");
        }

        var fileInfo = new FileInfo(candidate);
        if (!fileInfo.Exists)
        {
            throw new FileNotFoundException("A bundled payload file is missing.", candidate);
        }

        if (fileInfo.Length != file.Size)
        {
            throw new InvalidDataException($"Bundled payload size mismatch: '{file.Path}'.");
        }

        await using var stream = new FileStream(
            candidate,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var digest = await SHA256.HashDataAsync(stream, cancellationToken);
        var actual = Convert.ToHexString(digest);

        if (!actual.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Bundled payload hash mismatch: '{file.Path}'.");
        }
    }
}
