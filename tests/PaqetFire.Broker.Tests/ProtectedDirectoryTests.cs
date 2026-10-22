using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using PaqetFire.Broker.Configuration;
using PaqetFire.Broker.RoutingData;
using Xunit;

namespace PaqetFire.Broker.Tests;

/// <summary>
/// Planted directories, junctions, and hard links in broker state folders. These run
/// without elevation using the current account as the trusted owner; the owner-takeover
/// case needs an elevated run.
/// </summary>
public sealed class ProtectedDirectoryTests : IDisposable
{
    private readonly string scratch = Path.Combine(Path.GetTempPath(), "PaqetFire-protected", Guid.NewGuid().ToString("N"));

    public ProtectedDirectoryTests() => Directory.CreateDirectory(scratch);

    [Fact]
    public void ANewDirectoryGetsTheOwnerAndAProtectedDacl()
    {
        var policy = ProtectedDirectoryPolicy.CurrentUser();
        var directory = new ProtectedDirectory(Path.Combine(scratch, "data"), policy);

        Assert.True(directory.Ensure());
        Assert.False(directory.Ensure());

        var security = new DirectoryInfo(directory.Path).GetAccessControl();
        Assert.True(security.AreAccessRulesProtected);
        Assert.Equal(policy.Owner, security.GetOwner(typeof(SecurityIdentifier)));
        var rule = Assert.Single(security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>());
        Assert.Equal(policy.Owner, rule.IdentityReference);
        Assert.False(rule.IsInherited);
    }

    [Fact]
    public void TheMachineDataPolicyLetsUsersReadOnly()
    {
        var policy = ProtectedDirectoryPolicy.MachineData;
        Assert.Equal(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), policy.Owner);
        Assert.True(policy.IsTrusted(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null)));
        Assert.False(policy.IsTrusted(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null)));
        Assert.False(policy.IsTrusted(null));
        var users = Assert.Single(policy.Access, entry => entry.Identity.IsWellKnown(WellKnownSidType.BuiltinUsersSid));
        Assert.Equal(0, (int)(users.Rights & (FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.ChangePermissions)));
        Assert.DoesNotContain(ProtectedDirectoryPolicy.BrokerPrivate.Access, entry => entry.Identity.IsWellKnown(WellKnownSidType.BuiltinUsersSid));
    }

    [Fact]
    public void AJunctionAtThePathIsReplacedAndItsTargetLeftAlone()
    {
        var target = Path.Combine(scratch, "target");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "keep.txt"), "keep");
        var path = Path.Combine(scratch, "routing-data");
        FileSystemLinks.CreateJunction(path, target);
        var directory = new ProtectedDirectory(path, ProtectedDirectoryPolicy.CurrentUser());

        Assert.True(directory.Ensure());

        Assert.Equal(0, (int)(File.GetAttributes(path) & FileAttributes.ReparsePoint));
        Assert.Empty(Directory.EnumerateFileSystemEntries(path));
        Assert.True(File.Exists(Path.Combine(target, "keep.txt")));
        Assert.False(new DirectoryInfo(target).GetAccessControl().AreAccessRulesProtected);
    }

    [Fact]
    public void PlantedEntriesAreRemovedWithoutFollowingLinks()
    {
        var outside = Path.Combine(scratch, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "victim.txt"), "victim");
        var directory = new ProtectedDirectory(Path.Combine(scratch, "PaqetFire"), ProtectedDirectoryPolicy.CurrentUser(replaceUntrusted: false));
        directory.Ensure();
        FileSystemLinks.CreateJunction(Path.Combine(directory.Path, "routing-data"), outside);
        FileSystemLinks.CreateHardLink(Path.Combine(directory.Path, "settings.json"), Path.Combine(outside, "victim.txt"));
        File.WriteAllText(Path.Combine(directory.Path, "profiles.json"), "{}");

        var removed = directory.RemoveUntrustedEntries();

        Assert.Equal(["routing-data", "settings.json"], removed.Order(StringComparer.Ordinal));
        Assert.True(File.Exists(Path.Combine(directory.Path, "profiles.json")));
        Assert.Equal("victim", File.ReadAllText(Path.Combine(outside, "victim.txt")));
    }

    [Fact]
    public void DeletingADirectoryNeverWalksIntoAJunctionInsideIt()
    {
        var outside = Path.Combine(scratch, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "victim.txt"), "victim");
        var release = Path.Combine(scratch, "release");
        Directory.CreateDirectory(release);
        File.WriteAllText(Path.Combine(release, "geoip.dat"), "data");
        FileSystemLinks.CreateJunction(Path.Combine(release, "link"), outside);

        RoutingDataStore.TryDeleteDirectory(release);

        Assert.False(Directory.Exists(release));
        Assert.True(File.Exists(Path.Combine(outside, "victim.txt")));
    }

    [Fact]
    public void StoresRevalidateTheirDirectoryBeforeUse()
    {
        var target = Path.Combine(scratch, "target");
        Directory.CreateDirectory(target);
        var path = Path.Combine(scratch, "PaqetFire");
        FileSystemLinks.CreateJunction(path, target);
        var directory = new ProtectedDirectory(path, ProtectedDirectoryPolicy.CurrentUser(replaceUntrusted: false));

        Assert.False(new Configuration.BrokerStateStore(Path.Combine(path, "broker-state.json"), directory).Load().DesiredConnected);

        Assert.Equal(0, (int)(File.GetAttributes(path) & FileAttributes.ReparsePoint));
        Assert.Throws<ArgumentException>(() => ProtectedDirectory.EnsureFor(directory, Path.Combine(target, "x.json")));
    }

    [Fact]
    public void AStateFileNobodyTrustedWroteIsIgnored()
    {
        var root = Path.Combine(scratch, "routing-data");
        var store = new RoutingDataStore(root, ProtectedDirectoryPolicy.CurrentUser());
        store.Load();
        var outside = Path.Combine(scratch, "planted.json");
        File.WriteAllText(outside, """{"automaticUpdates":false}""");
        FileSystemLinks.CreateHardLink(Path.Combine(root, "state.json"), outside);

        Assert.True(store.Load().AutomaticUpdates);
    }

    [ElevatedFact]
    public void ARootAnotherAccountOwnsIsMovedAsideAndItsStateDiscarded()
    {
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        var root = Path.Combine(scratch, "routing-data");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "state.json"), """{"automaticUpdates":false}""");
        var planted = new ProtectedDirectory(root, new ProtectedDirectoryPolicy(
            users, [users], [(administrators, FileSystemRights.FullControl), (users, FileSystemRights.FullControl)], ReplaceUntrusted: false));
        planted.Ensure();

        var store = new RoutingDataStore(root, new ProtectedDirectoryPolicy(
            administrators, [administrators], [(administrators, FileSystemRights.FullControl)], ReplaceUntrusted: true));

        Assert.True(store.Load().AutomaticUpdates);
        Assert.Single(Directory.GetDirectories(scratch, "routing-data.untrusted-*"));
        Assert.Equal(administrators, new DirectoryInfo(root).GetAccessControl().GetOwner(typeof(SecurityIdentifier)));
    }

    public void Dispose()
    {
        try
        {
            RoutingDataStore.TryDeleteDirectory(scratch);
            if (Directory.Exists(scratch))
            {
                Directory.Delete(scratch, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Best effort for a temporary directory.
        }
    }
}

internal static class FileSystemLinks
{
    public static void CreateJunction(string link, string target)
    {
        using var mklink = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        mklink.WaitForExit();
        Assert.Equal(0, mklink.ExitCode);
    }

    public static void CreateHardLink(string link, string existing)
    {
        if (!CreateHardLinkW(link, existing, IntPtr.Zero))
        {
            throw new IOException($"The hard link could not be created ({Marshal.GetLastWin32Error()}).");
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string link, string existing, IntPtr securityAttributes);
}
