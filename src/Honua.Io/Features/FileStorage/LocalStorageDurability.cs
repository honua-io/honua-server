// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Honua.FileStorage;

// The configured local budget domain is Linux-only. Flush every affected parent
// directory as well as file bytes before acknowledging a durable object mutation.
internal static partial class LocalStorageDurability
{
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
}
