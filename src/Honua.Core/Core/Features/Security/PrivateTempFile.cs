// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Security;

// Scratch files and directories that other local users cannot read or pre-create. Files are
// created exclusively (O_EXCL, so an existing file or link is never followed) and owner-only on
// Unix. No Unix mode applies on Windows, where access follows the temp directory's ACL.
public static class PrivateTempFile
{
    public const UnixFileMode OwnerReadWrite = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    public const UnixFileMode OwnerDirectory = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    private const UnixFileMode SharedWrite = UnixFileMode.GroupWrite | UnixFileMode.OtherWrite;

    // ENOSPC and EDQUOT (Linux), ERROR_HANDLE_DISK_FULL and ERROR_DISK_FULL (Windows HRESULTs).
    private static readonly int[] StorageFullErrorCodes = [28, 122, unchecked((int)0x80070027), unchecked((int)0x80070070)];

    public static bool IsStorageFull(Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is IOException && Array.IndexOf(StorageFullErrorCodes, current.HResult) >= 0)
            {
                return true;
            }
        }

        return false;
    }

    // A fixed prefix, a fresh GUID and an optional extension directly under the temp path: the
    // name cannot be rooted or guessed, and no shared intermediate directory exists to squat.
    public static string NewPath(string prefix, string? extension = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        if (ContainsSeparator(prefix))
        {
            throw new ArgumentException("The prefix must be a file name segment.", nameof(prefix));
        }

        if (!string.IsNullOrEmpty(extension) && ContainsSeparator(extension))
        {
            throw new ArgumentException("The extension must not contain path separators.", nameof(extension));
        }

        var directory = Path.GetTempPath();
        CreateDirectory(directory);
        return Path.Join(directory, $"{prefix}{Guid.NewGuid():N}{extension}");
    }

    public static FileStreamOptions ApplyPrivateCreateNew(FileStreamOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Mode = FileMode.CreateNew;
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = OwnerReadWrite;
        }

        return options;
    }

    public static FileStream Create(string path, FileOptions fileOptions = FileOptions.Asynchronous, int bufferSize = 81920)
        => new(path, ApplyPrivateCreateNew(new FileStreamOptions
        {
            Access = FileAccess.Write,
            Share = FileShare.None,
            BufferSize = bufferSize,
            Options = fileOptions,
        }));

    // A read-write scratch file directly under the temp path, created exclusively and owner-only,
    // and deleted when the stream closes.
    public static FileStream CreateScratch(string prefix, string? extension, int bufferSize)
        => new(NewPath(prefix, extension), ApplyPrivateCreateNew(new FileStreamOptions
        {
            Access = FileAccess.ReadWrite,
            Share = FileShare.None,
            BufferSize = bufferSize,
            Options = FileOptions.Asynchronous | FileOptions.DeleteOnClose,
        }));

    // A newly created leaf directory is owner-only on Unix; missing parents get the default mode
    // and an existing directory keeps its mode.
    public static DirectoryInfo CreateDirectory(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return OperatingSystem.IsWindows()
            ? Directory.CreateDirectory(path)
            : Directory.CreateDirectory(path, OwnerDirectory);
    }

    // Creates the directory owner-only, or takes an existing one only if this process owns it:
    // group and other write are removed, and changing the mode fails for any other owner, so a
    // directory another user pre-created (who could replace files in it) is refused. A process
    // running as root can change any mode, so this check does not detect foreign ownership there.
    public static DirectoryInfo CreateOrClaimDirectory(string path)
    {
        var directory = CreateDirectory(path);
        if (OperatingSystem.IsWindows())
        {
            return directory;
        }

        var mode = File.GetUnixFileMode(path);
        try
        {
            File.SetUnixFileMode(path, mode & ~SharedWrite);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new InvalidOperationException(
                $"Directory '{path}' is owned by another user; configure a directory owned by the service user.", ex);
        }

        return directory;
    }

    public static bool IsWritableByOthers(string path)
    {
        if (OperatingSystem.IsWindows() || !Directory.Exists(path))
        {
            return false;
        }

        return (File.GetUnixFileMode(path) & SharedWrite) != 0;
    }

    private static bool ContainsSeparator(string value)
        => value.Contains('/', StringComparison.Ordinal) || value.Contains('\\', StringComparison.Ordinal);
}
