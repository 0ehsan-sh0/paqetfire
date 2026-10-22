using System.Security.AccessControl;
using System.Security.Principal;

namespace PaqetFire.Broker.Configuration;

/// <param name="Owner">The owner applied to the directory.</param>
/// <param name="TrustedOwners">Owners whose files and directories the broker accepts.</param>
/// <param name="Access">The complete, protected DACL; nothing is inherited from the parent.</param>
/// <param name="ReplaceUntrusted">
/// Move an existing directory with another owner aside and start empty. Otherwise the
/// directory is taken over, keeping its contents.
/// </param>
public sealed record ProtectedDirectoryPolicy(
    SecurityIdentifier Owner,
    IReadOnlyList<SecurityIdentifier> TrustedOwners,
    IReadOnlyList<(SecurityIdentifier Identity, FileSystemRights Rights)> Access,
    bool ReplaceUntrusted)
{
    private static readonly SecurityIdentifier LocalSystem = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier Users = new(WellKnownSidType.BuiltinUsersSid, null);

    /// <summary>
    /// <c>%ProgramData%\PaqetFire</c>: users may read it, only SYSTEM and Administrators may
    /// write. Files inside carry their own SYSTEM/Administrators-only DACL.
    /// </summary>
    public static ProtectedDirectoryPolicy MachineData { get; } = new(
        LocalSystem,
        [LocalSystem, Administrators],
        [(LocalSystem, FileSystemRights.FullControl), (Administrators, FileSystemRights.FullControl),
         (Users, FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize)],
        ReplaceUntrusted: false);

    /// <summary>A broker-only directory whose contents are discarded unless SYSTEM or Administrators own it.</summary>
    public static ProtectedDirectoryPolicy BrokerPrivate { get; } = new(
        LocalSystem,
        [LocalSystem, Administrators],
        [(LocalSystem, FileSystemRights.FullControl), (Administrators, FileSystemRights.FullControl)],
        ReplaceUntrusted: true);

    /// <summary>The same rules for the current account, so tests can run without elevation.</summary>
    internal static ProtectedDirectoryPolicy CurrentUser(bool replaceUntrusted = true)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User!;
        // An elevated test process creates files owned by Administrators.
        return new(user, [user, Administrators], [(user, FileSystemRights.FullControl)], replaceUntrusted);
    }

    public bool IsTrusted(SecurityIdentifier? owner) => owner is not null && TrustedOwners.Contains(owner);
}

/// <summary>
/// A directory that holds broker state. <see cref="Ensure"/> makes sure the path is a real
/// directory, not a junction or symbolic link, owned by a trusted account and carrying the
/// policy's protected DACL. Ownership and security are applied through a handle opened
/// without following reparse points, so a path swapped underneath cannot redirect them.
/// </summary>
public sealed class ProtectedDirectory
{
    private readonly Lock sync = new();
    private bool hardened;

    public ProtectedDirectory(string path, ProtectedDirectoryPolicy policy)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!System.IO.Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException("A protected directory must be an absolute path.", nameof(path));
        }

        Path = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path));
        Policy = policy ?? throw new ArgumentNullException(nameof(policy));
    }

    public string Path { get; }

    public ProtectedDirectoryPolicy Policy { get; }

    /// <summary>
    /// Re-validates the directory that holds <paramref name="filePath"/> before a store uses
    /// it. Without one (tests), the parent is simply created.
    /// </summary>
    internal static void EnsureFor(ProtectedDirectory? directory, string filePath)
    {
        var parent = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(filePath))!;
        if (directory is null)
        {
            Directory.CreateDirectory(parent);
            return;
        }

        if (!string.Equals(System.IO.Path.TrimEndingDirectorySeparator(parent), directory.Path, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The file is not inside the protected directory.", nameof(filePath));
        }

        directory.Ensure();
    }

    /// <returns>
    /// True when the directory was created or replaced just now, so nothing found at the
    /// path before may be trusted.
    /// </returns>
    public bool Ensure()
    {
        lock (sync)
        {
            var fresh = false;
            for (var attempt = 0; attempt < 4; attempt++)
            {
                if (SecureFileSystem.IsAbsent(Path))
                {
                    new DirectoryInfo(Path).Create(CreateSecurity());
                    fresh = true;
                }

                using var handle = SecureFileSystem.Open(Path, forSecurityChange: true);
                var entry = SecureFileSystem.Inspect(handle);
                if (entry.IsReparsePoint || !entry.IsDirectory ||
                    (Policy.ReplaceUntrusted && !Policy.IsTrusted(entry.Owner)))
                {
                    // The parent's delete-child right is enough to move it, whatever its own
                    // DACL says; nothing is applied to it, so a hard link cannot redirect a change.
                    handle.Dispose();
                    MoveAside(entry);
                    hardened = false;
                    fresh = true;
                    continue;
                }

                if (fresh || !hardened || !Policy.IsTrusted(entry.Owner))
                {
                    SecureFileSystem.Apply(handle, Policy, inheritable: true);
                    if (!Policy.IsTrusted(SecureFileSystem.Inspect(handle).Owner))
                    {
                        throw new UnauthorizedAccessException($"'{Path}' could not be given a trusted owner.");
                    }
                }

                hardened = true;
                return fresh;
            }

            throw new UnauthorizedAccessException($"'{Path}' kept being replaced and could not be secured.");
        }
    }

    /// <summary>
    /// Removes direct children nobody trusted created: links, hard-linked files, and files or
    /// directories with another owner. Call after <see cref="Ensure"/>, once nobody else can
    /// add entries.
    /// </summary>
    /// <returns>The names that were removed or moved aside.</returns>
    public IReadOnlyList<string> RemoveUntrustedEntries()
    {
        var removed = new List<string>();
        foreach (var child in Directory.EnumerateFileSystemEntries(Path))
        {
            var name = System.IO.Path.GetFileName(child);
            if (name.Contains(".untrusted-", StringComparison.Ordinal))
            {
                continue;
            }

            SecureEntry entry;
            try
            {
                entry = SecureFileSystem.Inspect(child);
            }
            catch (FileNotFoundException)
            {
                continue;
            }

            if (entry.IsReparsePoint || (!entry.IsDirectory && entry.Links != 1) || !Policy.IsTrusted(entry.Owner))
            {
                if (entry.IsDirectory && !entry.IsReparsePoint)
                {
                    // Its contents may still be writable by whoever created it; keep it inert
                    // instead of walking into it.
                    Directory.Move(child, $"{child}.untrusted-{Guid.NewGuid():N}");
                }
                else
                {
                    SecureFileSystem.DeleteWithoutFollowing(child);
                }

                removed.Add(name);
            }
        }

        return removed;
    }

    private void MoveAside(SecureEntry entry)
    {
        if (entry.IsReparsePoint)
        {
            // Removing a link leaves its target untouched.
            SecureFileSystem.DeleteWithoutFollowing(Path);
        }
        else if (entry.IsDirectory)
        {
            Directory.Move(Path, $"{Path}.untrusted-{Guid.NewGuid():N}");
        }
        else
        {
            File.Move(Path, $"{Path}.untrusted-{Guid.NewGuid():N}");
        }
    }

    private DirectorySecurity CreateSecurity()
    {
        var security = new DirectorySecurity();
        security.SetOwner(Policy.Owner);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var (identity, rights) in Policy.Access)
        {
            security.AddAccessRule(new FileSystemAccessRule(
                identity,
                rights,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
        }

        return security;
    }
}
