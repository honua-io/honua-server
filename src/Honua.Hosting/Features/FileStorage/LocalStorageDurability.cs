// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Honua.FileStorage;

// Configured local budget domains are Linux-only. Flush every affected parent
// directory as well as file bytes before acknowledging a durable object mutation.
internal static partial class LocalStorageDurability
{
    internal static long ReadRegularFileLength(string path)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Local census requires Linux statx.");
        // AT_FDCWD, AT_SYMLINK_NOFOLLOW, STATX_TYPE | STATX_SIZE. This does
        // not open content: a FIFO or socket must never block a recovery census.
        if (Statx(-100, path, 0x100, 0x201, out var status) != 0)
            throw new IOException($"The physical object could not be inspected (errno {Marshal.GetLastPInvokeError()}).");
        if ((status.Mask & 0x201) != 0x201 || (status.Mode & 0xf000) != 0x8000 || status.Size > long.MaxValue)
            throw new IOException("A census object must be a regular file with a known length.");
        return (long)status.Size;
    }

    internal static void FlushDirectoryTree(string directory, string root)
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("Fenced local storage durability requires Linux directory fsync.");
        var boundary = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var current = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        if (current != boundary && !current.StartsWith(boundary + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new IOException("A durable object directory must remain inside its configured domain.");
        while (true)
        {
            // O_RDONLY | O_DIRECTORY | O_CLOEXEC | O_NOFOLLOW. The host must own
            // the complete domain namespace; workers cannot replace its parents.
            using var handle = OpenDirectory(current, 65536 | 524288 | 131072);
            if (handle.IsInvalid || Fsync(handle) != 0)
                throw new IOException($"The storage directory could not be synchronized (errno {Marshal.GetLastPInvokeError()}).");
            if (current == boundary) return;
            current = Path.GetDirectoryName(current) ?? throw new IOException("The storage domain boundary disappeared.");
        }
    }

    [LibraryImport("libc", EntryPoint = "open", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial SafeFileHandle OpenDirectory(string path, int flags);

    [LibraryImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static partial int Fsync(SafeFileHandle handle);

    [LibraryImport("libc", EntryPoint = "statx", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int Statx(int directory, string path, int flags, uint mask, out LinuxStatx status);

    // Fixed Linux UAPI layout, including the reserved tail so the kernel cannot
    // overwrite a truncated managed buffer. Only requested fields are consumed.
    // https://github.com/torvalds/linux/blob/master/include/uapi/linux/stat.h
    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct LinuxStatx
    {
        [FieldOffset(0)] internal uint Mask;
        [FieldOffset(28)] internal ushort Mode;
        [FieldOffset(40)] internal ulong Size;
    }
}
