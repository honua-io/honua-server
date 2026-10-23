// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Runtime.InteropServices;
using Honua.FileStorage;
using Microsoft.Win32.SafeHandles;

namespace Honua.Import.FileImport.Resumable;

// Linux local/shared-filesystem support only. The host must attest that every
// process uses the same stable inode and that the filesystem implements flock.
// Lock files are never replaced/deleted. Each nested lease owns a distinct open
// file description, so shared nesting cannot deadlock an exclusive census.
internal sealed partial class LocalStorageDomainLock
{
    private readonly string _path;

    internal LocalStorageDomainLock(string configuredJournalDirectory, string lockFileName = "domain.lock")
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Local storage-domain fencing requires Linux flock.");
        ArgumentException.ThrowIfNullOrWhiteSpace(configuredJournalDirectory);
        if (string.IsNullOrWhiteSpace(lockFileName) || lockFileName.Length > 128 ||
            lockFileName.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '.')) ||
            lockFileName is "." or "..")
            throw new ArgumentException("The control lock name must be one bounded filename.", nameof(lockFileName));
        if (!Path.IsPathFullyQualified(configuredJournalDirectory)) throw new ArgumentException("The journal root must be absolute.");
        var directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(configuredJournalDirectory));
        Directory.CreateDirectory(directory);
        RejectLink(directory);
        _path = Path.Join(directory, lockFileName);
        if (File.Exists(_path)) RejectLink(_path);
    }

    internal async ValueTask<IAsyncDisposable> EnterAsync(bool exclusive, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RejectLink(Path.GetDirectoryName(_path)!);
        if (File.Exists(_path)) RejectLink(_path);
        // File.OpenHandle applies its own nonblocking sharing flock on Linux.
        // That can fail before our cancellable wait when another lease holds
        // LOCK_EX. Open a distinct native description without a second lock.
        // O_RDWR | O_CREAT | O_CLOEXEC | O_NOFOLLOW, owner read/write only.
        var handle = OpenLock(_path, 2 | 64 | 524288 | 131072, 384);
        try
        {
            if (handle.IsInvalid)
                throw new IOException($"The storage-domain control file could not be opened (errno {Marshal.GetLastPInvokeError()}).");
            if (LocalStorageDurability.ReadRegularFileLength(_path) != 0)
                throw new IOException("The storage-domain control file must be an empty regular file.");
            RandomAccess.FlushToDisk(handle);
            LocalStorageDurability.FlushDirectoryTree(Path.GetDirectoryName(_path)!, Path.GetDirectoryName(_path)!);
            while (Flock(handle, (exclusive ? 2 : 1) | 4) != 0)
            {
                var error = Marshal.GetLastPInvokeError();
                if (error is not (11 or 4)) throw new IOException($"The storage-domain fence could not be acquired (errno {error}).");
                await Task.Delay(25, cancellationToken).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            return new Lease(handle);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static void RejectLink(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("A storage-domain control path cannot be a symbolic link.");
    }

    // Closing an independently opened description releases only this lease's flock.
    private sealed class Lease(SafeFileHandle handle) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            handle.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    [LibraryImport("libc", EntryPoint = "flock", SetLastError = true)]
    private static partial int Flock(SafeFileHandle file, int operation);

    [LibraryImport("libc", EntryPoint = "open", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial SafeFileHandle OpenLock(string path, int flags, uint mode);
}
