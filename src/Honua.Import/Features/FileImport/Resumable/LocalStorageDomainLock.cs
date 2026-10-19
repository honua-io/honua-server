// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Honua.Import.FileImport.Resumable;

// Linux local/shared-filesystem support only. The host must attest that every
// process uses the same stable inode and that the filesystem implements flock.
// Lock files are never replaced/deleted. Each nested lease owns a distinct open
// file description, so shared nesting cannot deadlock an exclusive census.
internal sealed partial class LocalStorageDomainLock
{
    private readonly string _path;

    internal LocalStorageDomainLock(string configuredJournalDirectory)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Local storage-domain fencing requires Linux flock.");
        ArgumentException.ThrowIfNullOrWhiteSpace(configuredJournalDirectory);
        if (!Path.IsPathFullyQualified(configuredJournalDirectory)) throw new ArgumentException("The journal root must be absolute.");
        var directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(configuredJournalDirectory));
        Directory.CreateDirectory(directory);
        RejectLink(directory);
        _path = Path.Join(directory, "domain.lock");
        if (File.Exists(_path)) RejectLink(_path);
    }

    internal async ValueTask<IAsyncDisposable> EnterAsync(bool exclusive, CancellationToken cancellationToken)
    {
        RejectLink(Path.GetDirectoryName(_path)!);
        if (File.Exists(_path)) RejectLink(_path);
        var handle = File.OpenHandle(_path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite,
            FileOptions.WriteThrough);
        try
        {
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
}
