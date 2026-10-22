using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PaqetFire.Broker.Deployment;

/// <summary>
/// Volume, file ID, size, and last write time. While all four are unchanged, a file that
/// already passed a hash check need not be hashed again.
/// </summary>
internal sealed record FileIdentity(uint Volume, ulong FileId, long Size, long LastWrite)
{
    public static FileIdentity? TryRead(string path)
    {
        try
        {
            using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return TryRead(handle);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Attributes and hard-link count of the object the handle refers to (a link itself when opened without following it).</summary>
    public static bool TryReadDetails(SafeFileHandle handle, out FileAttributes attributes, out uint links)
    {
        if (GetFileInformationByHandle(handle, out var information))
        {
            attributes = (FileAttributes)information.FileAttributes;
            links = information.NumberOfLinks;
            return true;
        }

        attributes = 0;
        links = 0;
        return false;
    }

    public static FileIdentity? TryRead(SafeFileHandle handle) =>
        GetFileInformationByHandle(handle, out var information)
            ? new FileIdentity(
                information.VolumeSerialNumber,
                ((ulong)information.FileIndexHigh << 32) | information.FileIndexLow,
                ((long)information.FileSizeHigh << 32) | information.FileSizeLow,
                ((long)information.LastWriteTimeHigh << 32) | information.LastWriteTimeLow)
            : null;

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public uint CreationTimeLow;
        public uint CreationTimeHigh;
        public uint LastAccessTimeLow;
        public uint LastAccessTimeHigh;
        public uint LastWriteTimeLow;
        public uint LastWriteTimeHigh;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation information);
}
