// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.IO.Compression;

namespace Honua.Import.Features.I3sImport;

/// <summary>
/// Reads typed entries out of an Esri <c>.slpk</c> ZIP archive. .slpk entries
/// may be stored plain (<c>file.ext</c>) or gzipped (<c>file.ext.gz</c>); this
/// helper resolves either form and transparently decompresses gzipped entries.
/// </summary>
internal sealed class I3sSlpkReader : IDisposable
{
    private readonly ZipArchive _archive;
    private readonly Dictionary<string, ZipArchiveEntry> _entriesByLogicalPath;

    private I3sSlpkReader(ZipArchive archive, Dictionary<string, ZipArchiveEntry> entriesByLogicalPath)
    {
        _archive = archive;
        _entriesByLogicalPath = entriesByLogicalPath;
    }

    /// <summary>
    /// Opens a <c>.slpk</c> file from disk.
    /// </summary>
    public static I3sSlpkReader Open(string slpkPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(slpkPath);

        var fileStream = new FileStream(slpkPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return OpenCore(fileStream, leaveOpen: false);
    }

    /// <summary>
    /// Opens a <c>.slpk</c> file from an already-positioned stream. The stream is owned by the reader.
    /// </summary>
    public static I3sSlpkReader Open(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return OpenCore(stream, leaveOpen: false);
    }

    private static I3sSlpkReader OpenCore(Stream stream, bool leaveOpen)
    {
        var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen);
        var entriesByLogicalPath = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.FullName) || entry.FullName.EndsWith('/'))
            {
                continue;
            }

            var normalized = NormalizePath(entry.FullName);
            var logicalPath = normalized.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)
                ? normalized[..^3]
                : normalized;

            // First-wins on duplicates (rare in well-formed .slpk archives; common
            // pattern is that the .gz variant is the only one shipped). Indexing
            // tolerates the unusual case where both variants exist.
            _ = entriesByLogicalPath.TryAdd(logicalPath, entry);
        }

        return new I3sSlpkReader(archive, entriesByLogicalPath);
    }

    /// <summary>
    /// Returns true when an entry with the supplied logical path (extension
    /// without trailing <c>.gz</c>) exists.
    /// </summary>
    public bool ContainsEntry(string logicalPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logicalPath);
        return _entriesByLogicalPath.ContainsKey(NormalizePath(logicalPath));
    }

    /// <summary>
    /// Opens a stream for the supplied logical path. Gzipped entries are
    /// wrapped in a <see cref="GZipStream"/>; plain entries are returned as-is.
    /// </summary>
    public Stream OpenEntry(string logicalPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logicalPath);
        var normalizedPath = NormalizePath(logicalPath);
        if (!_entriesByLogicalPath.TryGetValue(normalizedPath, out var entry))
        {
            throw new FileNotFoundException(
                $"I3S .slpk does not contain expected entry '{logicalPath}'.",
                logicalPath);
        }

        var raw = entry.Open();
        return entry.FullName.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)
            ? new GZipStream(raw, CompressionMode.Decompress)
            : raw;
    }

    /// <summary>
    /// Reads the entire entry payload into a byte array.
    /// </summary>
    public byte[] ReadAllBytes(string logicalPath)
    {
        using var stream = OpenEntry(logicalPath);
        using var memoryStream = new MemoryStream();
        stream.CopyTo(memoryStream);
        return memoryStream.ToArray();
    }

    /// <summary>
    /// Releases the underlying ZIP archive and stream.
    /// </summary>
    public void Dispose() => _archive.Dispose();

    private static string NormalizePath(string path)
        => path.Replace('\\', '/').TrimStart('/');
}
