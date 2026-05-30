// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.FileStorage;

internal static class CloudStoragePath
{
    private static readonly char[] _folderSeparators = ['/', '\\'];

    internal static string GenerateFileId() =>
        Guid.NewGuid().ToString("N");

    internal static string BuildObjectKey(string fileId, string fileName, string? folder, string? prefix)
    {
        var extension = Path.GetExtension(fileName);
        var storageName = $"{fileId}{extension}";
        var combined = Combine(NormalizeFolder(prefix, nameof(prefix)), NormalizeFolder(folder, nameof(folder)));

        return string.IsNullOrEmpty(combined)
            ? storageName
            : $"{combined}/{storageName}";
    }

    internal static string? BuildPrefix(string? folder, string? prefix)
    {
        var combined = Combine(NormalizeFolder(prefix, nameof(prefix)), NormalizeFolder(folder, nameof(folder)));
        if (string.IsNullOrEmpty(combined))
        {
            return null;
        }

        return combined.EndsWith('/')
            ? combined
            : $"{combined}/";
    }

    internal static string GetFileNameFromKey(string key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return string.Empty;
        }

        var normalized = key.Replace('\\', '/');
        var lastSlash = normalized.LastIndexOf('/');
        return lastSlash >= 0 ? normalized[(lastSlash + 1)..] : normalized;
    }

    private static string Combine(string prefix, string folder)
    {
        if (string.IsNullOrEmpty(prefix))
        {
            return folder;
        }

        if (string.IsNullOrEmpty(folder))
        {
            return prefix;
        }

        return $"{prefix}/{folder}";
    }

    private static string NormalizeFolder(string? folder, string paramName)
    {
        if (string.IsNullOrWhiteSpace(folder))
        {
            return string.Empty;
        }

        var trimmed = folder.Trim();
        if (trimmed.StartsWith('/') || trimmed.StartsWith('\\'))
        {
            throw new ArgumentException("Folder must be a relative path.", paramName);
        }

        trimmed = trimmed.Replace('\\', '/').Trim('/');
        ValidateFolderPath(trimmed, paramName);

        return trimmed;
    }

    private static void ValidateFolderPath(string folder, string paramName)
    {
        if (string.IsNullOrWhiteSpace(folder))
        {
            return;
        }

        if (Path.IsPathRooted(folder))
        {
            throw new ArgumentException("Folder must be a relative path.", paramName);
        }

        if (folder.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
        {
            throw new ArgumentException("Folder contains invalid path characters.", paramName);
        }

        var segments = folder.Split(_folderSeparators, StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(segment => segment == ".."))
        {
            throw new ArgumentException("Folder must not contain relative traversal segments.", paramName);
        }
    }
}
