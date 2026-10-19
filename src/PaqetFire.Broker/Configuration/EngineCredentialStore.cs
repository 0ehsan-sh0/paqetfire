using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PaqetFire.Core.Configuration;

namespace PaqetFire.Broker.Configuration;

/// <summary>
/// Persists the loopback engine credentials. They must survive broker restarts because
/// the startup kill-switch guard can keep ProxiFyre running with an earlier configuration
/// while Xray and Paqet are restarted from a newly written one.
/// </summary>
public sealed class EngineCredentialStore(string path)
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("PaqetFire.EngineSocksCredentials.v1");

    private readonly string _path = Path.GetFullPath(path);

    public EngineSocksCredentials LoadOrCreate()
    {
        if (TryLoad() is { } existing)
        {
            return existing;
        }

        var created = EngineSocksCredentials.Generate();
        Save(created);
        return created;
    }

    private EngineSocksCredentials? TryLoad()
    {
        if (!File.Exists(_path))
        {
            return null;
        }

        try
        {
            var stored = JsonSerializer.Deserialize<StoredCredentials>(File.ReadAllText(_path));
            if (stored is null || string.IsNullOrEmpty(stored.Username) || string.IsNullOrEmpty(stored.ProtectedPassword))
            {
                return null;
            }

            var clear = ProtectedData.Unprotect(
                Convert.FromBase64String(stored.ProtectedPassword),
                Entropy,
                DataProtectionScope.LocalMachine);
            try
            {
                return new EngineSocksCredentials(stored.Username, Encoding.UTF8.GetString(clear));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(clear);
            }
        }
        catch (Exception exception) when (
            exception is JsonException or FormatException or CryptographicException or IOException)
        {
            // Engine configurations are rewritten before every start, so a replacement is safe.
            return null;
        }
    }

    private void Save(EngineSocksCredentials credentials)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var clear = Encoding.UTF8.GetBytes(credentials.Password);
        string protectedPassword;
        try
        {
            protectedPassword = Convert.ToBase64String(
                ProtectedData.Protect(clear, Entropy, DataProtectionScope.LocalMachine));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clear);
        }

        var temporary = _path + $".{Guid.NewGuid():N}.new";
        try
        {
            using (var stream = ProtectedConfigurationFile.CreateNew(temporary))
            {
                JsonSerializer.Serialize(stream, new StoredCredentials(credentials.Username, protectedPassword));
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, _path, overwrite: true);
            ProtectedConfigurationFile.Harden(_path);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private sealed record StoredCredentials(string Username, string ProtectedPassword);
}
