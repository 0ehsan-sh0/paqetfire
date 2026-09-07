using System.Security.AccessControl;
using System.Security.Principal;
using PaqetFire.Broker.Configuration;
using Xunit;

namespace PaqetFire.Broker.Tests;

public sealed class ProtectedConfigurationFileTests
{
    [Fact]
    public async Task SecretFileHasProtectedAclBeforeFirstByteAndAfterWriterCloses()
    {
        var directory = Path.Combine(Path.GetTempPath(), "PaqetFire-acl-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "secret.tmp");
        try
        {
            // A readable parent is deliberately retained to exercise the original boundary.
            var parentSecurity = new DirectoryInfo(directory).GetAccessControl();
            parentSecurity.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
                FileSystemRights.ReadAndExecute, InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(directory).SetAccessControl(parentSecurity);

            await using (var stream = ProtectedConfigurationFile.CreateNew(path))
            {
                AssertProtected(stream.GetAccessControl());
                await stream.WriteAsync("test-secret"u8.ToArray());
                await stream.FlushAsync();
                AssertProtected(stream.GetAccessControl());
            }
            AssertProtected(new FileInfo(path).GetAccessControl());

            using var identity = WindowsIdentity.GetCurrent();
            if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator) &&
                identity.User?.IsWellKnown(WellKnownSidType.LocalSystemSid) != true)
            {
                Assert.Throws<UnauthorizedAccessException>(() => File.ReadAllText(path));
            }
            else
            {
                Assert.Equal("test-secret", await File.ReadAllTextAsync(path));
            }
        }
        finally
        {
            // Owners may restore their test-file DACL for cleanup without changing product ACLs.
            if (File.Exists(path))
            {
                var cleanup = new FileSecurity();
                cleanup.SetAccessRuleProtection(true, false);
                cleanup.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!,
                    FileSystemRights.FullControl, AccessControlType.Allow));
                new FileInfo(path).SetAccessControl(cleanup);
                File.Delete(path);
            }
            Directory.Delete(directory);
        }
    }

    [Fact]
    public async Task ExistingFileIsNeverTruncatedOrFollowed()
    {
        var path = Path.Combine(Path.GetTempPath(), "PaqetFire-existing-" + Guid.NewGuid());
        await File.WriteAllTextAsync(path, "existing-data");
        try
        {
            Assert.Throws<IOException>(() => ProtectedConfigurationFile.CreateNew(path));
            Assert.Equal("existing-data", await File.ReadAllTextAsync(path));
        }
        finally { File.Delete(path); }
    }

    private static void AssertProtected(FileSecurity security)
    {
        Assert.True(security.AreAccessRulesProtected);
        var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>().ToArray();
        Assert.Equal(2, rules.Length);
        Assert.All(rules, rule =>
        {
            Assert.False(rule.IsInherited);
            Assert.Equal(AccessControlType.Allow, rule.AccessControlType);
            Assert.Equal(FileSystemRights.FullControl, rule.FileSystemRights);
            var sid = (SecurityIdentifier)rule.IdentityReference;
            Assert.True(sid.IsWellKnown(WellKnownSidType.LocalSystemSid) ||
                sid.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid));
        });
    }
}
