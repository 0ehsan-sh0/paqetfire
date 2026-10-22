using System.Security.Principal;
using PaqetFire.Broker.Configuration;
using Xunit;
using System.Runtime.CompilerServices;

namespace PaqetFire.Broker.Tests;

// The store writes SYSTEM/Administrators-only files, which a standard user cannot read back.
public sealed class ElevatedFactAttribute : FactAttribute
{
    public ElevatedFactAttribute(
        [CallerFilePath] string? sourceFilePath = null,
        [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
        {
            Skip = "Requires an elevated test run.";
        }
    }
}

public sealed class EngineCredentialStoreTests
{
    [ElevatedFact]
    public void CredentialsSurviveRestartsWithoutPlaintextAndCorruptionRotatesThem()
    {
        var directory = Path.Combine(Path.GetTempPath(), "PaqetFire-engine-" + Guid.NewGuid());
        var path = Path.Combine(directory, "engine-credentials.json");
        try
        {
            var created = new EngineCredentialStore(path).LoadOrCreate();
            var reloaded = new EngineCredentialStore(path).LoadOrCreate();

            Assert.Equal(created, reloaded);
            Assert.DoesNotContain(created.Password, File.ReadAllText(path), StringComparison.Ordinal);

            File.WriteAllText(path, "{ not json");
            var rotated = new EngineCredentialStore(path).LoadOrCreate();
            Assert.NotEqual(created.Password, rotated.Password);
            Assert.Equal(rotated, new EngineCredentialStore(path).LoadOrCreate());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
