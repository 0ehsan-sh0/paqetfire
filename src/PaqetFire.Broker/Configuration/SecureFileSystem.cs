using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using PaqetFire.Broker.Deployment;

namespace PaqetFire.Broker.Configuration;

/// <summary>What a handle opened without following reparse points refers to.</summary>
internal readonly record struct SecureEntry(FileAttributes Attributes, uint Links, SecurityIdentifier? Owner)
{
    public bool IsReparsePoint => (Attributes & FileAttributes.ReparsePoint) != 0;

    public bool IsDirectory => (Attributes & FileAttributes.Directory) != 0;
}

/// <summary>
/// Handle-based file-system operations for broker-owned state. Every open uses
/// FILE_FLAG_OPEN_REPARSE_POINT, so a junction or symbolic link is inspected and changed
/// as itself and never followed to its target.
/// </summary>
internal static class SecureFileSystem
{
    private const uint ReadControl = 0x00020000;
    private const uint WriteDac = 0x00040000;
    private const uint WriteOwner = 0x00080000;
    private const uint GenericRead = 0x80000000;
    private const uint FileReadAttributes = 0x00000080;
    private const uint BackupSemantics = 0x02000000;
    private const uint OpenReparsePoint = 0x00200000;
    private const uint OwnerSecurityInformation = 0x00000001;
    private const uint DaclSecurityInformation = 0x00000004;
    private const uint ProtectedDaclSecurityInformation = 0x80000000;
    private const int FileObject = 1;

    private static readonly Lazy<bool> PrivilegesEnabled = new(EnablePrivileges);

    /// <summary>True when nothing, not even a dangling link, exists at <paramref name="path"/>.</summary>
    public static bool IsAbsent(string path) => GetFileAttributes(path) == uint.MaxValue;

    /// <summary>Opens a file or directory for inspection, or also for changing its owner and DACL.</summary>
    public static SafeFileHandle Open(string path, bool forSecurityChange)
    {
        _ = PrivilegesEnabled.Value;
        var access = ReadControl | FileReadAttributes | (forSecurityChange ? WriteDac | WriteOwner : 0);
        var handle = CreateFile(path, access, FileShare.ReadWrite | FileShare.Delete, IntPtr.Zero, FileMode.Open,
            BackupSemantics | OpenReparsePoint, IntPtr.Zero);
        return handle.IsInvalid ? throw Failure(path) : handle;
    }

    /// <summary>
    /// Opens a file for reading after checking that it is a plain file with one link and a
    /// trusted owner. Writers are denied while the stream is open.
    /// </summary>
    public static FileStream OpenTrustedFileForRead(string path, ProtectedDirectoryPolicy policy)
    {
        _ = PrivilegesEnabled.Value;
        // No backup semantics: with SeBackupPrivilege enabled they would also bypass another
        // program's sharing mode, so a held file would look readable here but not to Xray.
        var handle = CreateFile(path, GenericRead | ReadControl, FileShare.Read, IntPtr.Zero, FileMode.Open,
            OpenReparsePoint, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            throw Failure(path);
        }

        try
        {
            var entry = Inspect(handle);
            if (entry.IsReparsePoint || entry.IsDirectory || entry.Links != 1 || !policy.IsTrusted(entry.Owner))
            {
                throw new InvalidDataException($"'{Path.GetFileName(path)}' is not a file PaqetFire created.");
            }

            return new FileStream(handle, FileAccess.Read, 128 * 1024, isAsync: false);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    public static SecureEntry Inspect(SafeFileHandle handle)
    {
        if (!FileIdentity.TryReadDetails(handle, out var attributes, out var links))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        return new SecureEntry(attributes, links, ReadOwner(handle));
    }

    public static SecureEntry Inspect(string path)
    {
        using var handle = Open(path, forSecurityChange: false);
        return Inspect(handle);
    }

    /// <summary>Sets the owner and a protected DACL through the handle, so a swapped path cannot redirect it.</summary>
    public static void Apply(SafeFileHandle handle, ProtectedDirectoryPolicy policy, bool inheritable)
    {
        var owner = new byte[policy.Owner.BinaryLength];
        policy.Owner.GetBinaryForm(owner, 0);
        var acl = new RawAcl(GenericAcl.AclRevision, policy.Access.Count);
        var flags = inheritable ? AceFlags.ContainerInherit | AceFlags.ObjectInherit : AceFlags.None;
        foreach (var (identity, rights) in policy.Access)
        {
            acl.InsertAce(acl.Count, new CommonAce(flags, AceQualifier.AccessAllowed, (int)rights, identity, false, null));
        }

        var dacl = new byte[acl.BinaryLength];
        acl.GetBinaryForm(dacl, 0);
        var result = SetSecurityInfo(handle, FileObject,
            OwnerSecurityInformation | DaclSecurityInformation | ProtectedDaclSecurityInformation,
            owner, IntPtr.Zero, dacl, IntPtr.Zero);
        if (result != 0)
        {
            throw new UnauthorizedAccessException("PaqetFire could not secure its data directory.", new Win32Exception(result));
        }
    }

    /// <summary>
    /// Deletes an entry without following links: a junction or symbolic link is removed as a
    /// link, and a directory's contents are each checked before they are deleted.
    /// </summary>
    public static void DeleteWithoutFollowing(string path)
    {
        if (IsAbsent(path))
        {
            return;
        }

        SecureEntry entry;
        using (var handle = Open(path, forSecurityChange: false))
        {
            entry = Inspect(handle);
        }

        if (entry.IsDirectory && !entry.IsReparsePoint)
        {
            foreach (var child in Directory.EnumerateFileSystemEntries(path))
            {
                DeleteWithoutFollowing(child);
            }
        }

        if (entry.IsDirectory)
        {
            // RemoveDirectory on a junction removes the link and leaves its target alone.
            Directory.Delete(path, recursive: false);
        }
        else
        {
            if ((entry.Attributes & FileAttributes.ReadOnly) != 0)
            {
                File.SetAttributes(path, entry.Attributes & ~FileAttributes.ReadOnly);
            }

            File.Delete(path);
        }
    }

    private static SecurityIdentifier? ReadOwner(SafeFileHandle handle)
    {
        GetKernelObjectSecurity(handle, OwnerSecurityInformation, null, 0, out var needed);
        if (needed == 0)
        {
            return null;
        }

        var descriptor = new byte[needed];
        if (!GetKernelObjectSecurity(handle, OwnerSecurityInformation, descriptor, needed, out _))
        {
            return null;
        }

        return new RawSecurityDescriptor(descriptor, 0).Owner;
    }

    private static Exception Failure(string path)
    {
        var error = Marshal.GetLastWin32Error();
        return error is 2 or 3
            ? new FileNotFoundException($"'{path}' was not found.", path)
            : new IOException($"'{path}' could not be opened.", new Win32Exception(error));
    }

    // SYSTEM holds these but may not have them enabled. With backup semantics they let the
    // broker take over an entry whose DACL tries to lock it out. Missing privileges (tests
    // without elevation) are not an error.
    private static bool EnablePrivileges()
    {
        if (!OpenProcessToken(GetCurrentProcess(), 0x0020 | 0x0008, out var token))
        {
            return false;
        }

        using (token)
        {
            var any = false;
            foreach (var name in new[] { "SeRestorePrivilege", "SeTakeOwnershipPrivilege", "SeBackupPrivilege" })
            {
                if (!LookupPrivilegeValue(null, name, out var luid))
                {
                    continue;
                }

                var privileges = new TokenPrivileges { Count = 1, Luid = luid, Attributes = 0x00000002 };
                any |= AdjustTokenPrivileges(token, false, ref privileges, 0, IntPtr.Zero, IntPtr.Zero) &&
                       Marshal.GetLastWin32Error() == 0;
            }

            return any;
        }
    }

    // TOKEN_PRIVILEGES with one LUID_AND_ATTRIBUTES; the LUID is only 4-byte aligned.
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct TokenPrivileges
    {
        public uint Count;
        public long Luid;
        public uint Attributes;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string fileName, uint access, FileShare share, IntPtr securityAttributes, FileMode mode, uint flags, IntPtr template);

    [DllImport("kernel32.dll", EntryPoint = "GetFileAttributesW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFileAttributes(string fileName);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetKernelObjectSecurity(
        SafeFileHandle handle, uint requested, byte[]? descriptor, uint length, out uint needed);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern int SetSecurityInfo(
        SafeFileHandle handle, int objectType, uint securityInformation, byte[] owner, IntPtr group, byte[] dacl, IntPtr sacl);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out SafeAccessTokenHandle token);

    [DllImport("advapi32.dll", EntryPoint = "LookupPrivilegeValueW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LookupPrivilegeValue(string? system, string name, out long luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AdjustTokenPrivileges(
        SafeAccessTokenHandle token, bool disableAll, ref TokenPrivileges state, uint length, IntPtr previous, IntPtr returned);
}
