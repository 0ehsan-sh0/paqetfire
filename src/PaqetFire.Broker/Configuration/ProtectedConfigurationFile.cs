using System.Security.AccessControl;
using System.Security.Principal;

namespace PaqetFire.Broker.Configuration;

internal static class ProtectedConfigurationFile
{
    // Supply the DACL to CreateFile: no plaintext or machine-scope DPAPI ciphertext
    // may exist with inherited read permissions, even if the broker crashes.
    internal static FileStream CreateNew(string path) => new FileInfo(path).Create(
        FileMode.CreateNew,
        FileSystemRights.FullControl,
        FileShare.None,
        64 * 1024,
        FileOptions.Asynchronous | FileOptions.WriteThrough,
        CreateSecurity());

    internal static void Harden(string path) => new FileInfo(path).SetAccessControl(CreateSecurity());

    private static FileSecurity CreateSecurity()
    {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
        {
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(sid, null), FileSystemRights.FullControl, AccessControlType.Allow));
        }
        return security;
    }
}
