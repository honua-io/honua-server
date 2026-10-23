// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Text;
using Honua.FileStorage;

namespace Honua.Import.FileImport.Resumable;

// Durable root markers distinguish an empty original root from a replacement
// directory or changed host configuration. The host owns this namespace; these
// markers do not attest a disk quota or authorize an untrusted filesystem owner.
internal sealed class LocalImportStorageRoots
{
    internal const string MarkerName = ".honua-import-root";
    private readonly Dictionary<ImportStorageRootKind, string> _roots;
    private readonly string _owner;

    internal LocalImportStorageRoots(IReadOnlyDictionary<ImportStorageRootKind, string> roots,
        string journalDirectory, string domainId)
    {
        _roots = roots.ToDictionary(pair => pair.Key,
            pair => Path.TrimEndingDirectorySeparator(Path.GetFullPath(pair.Value)));
        _owner = Path.GetFullPath(journalDirectory) + '\0' + domainId;
    }

    // Only bootstrap, after an exclusive raw census proves the roots empty.
    // A partial bootstrap may reuse valid markers, but cannot replace one.
    internal void Provision()
    {
        foreach (var (kind, root) in _roots)
        {
            var path = Path.Join(root, MarkerName);
            try { _ = File.GetAttributes(path); }
            catch (FileNotFoundException)
            {
                using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                file.Write(Encoding.ASCII.GetBytes(Owner(kind) + "." + Guid.NewGuid().ToString("N")));
                file.Flush(flushToDisk: true);
                LocalStorageDurability.FlushDirectoryTree(root, root);
            }
            _ = ReadMarker(kind);
        }
    }

    internal string Fingerprint()
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var (kind, root) in _roots.OrderBy(pair => pair.Key))
            hash.AppendData(Encoding.UTF8.GetBytes(Owner(kind) + '\0' + root + '\0' + ReadMarker(kind) + '\0'));
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    internal void RequireFingerprint(string? expected)
    {
        if (expected is null || Fingerprint() != expected)
            throw new InvalidDataException("The physical import roots differ from their durable journal binding.");
    }

    internal string ReadMarker(ImportStorageRootKind kind)
    {
        LocalImportStorageCensus.RequireDirectory(_roots[kind]);
        var path = Path.Join(_roots[kind], MarkerName);
        if (LocalStorageDurability.ReadRegularFileLength(path) != 97)
            throw new InvalidDataException("The physical import root marker is malformed.");
        using var file = File.OpenRead(path);
        Span<byte> bytes = stackalloc byte[98];
        var count = file.ReadAtLeast(bytes, 98, throwOnEndOfStream: false);
        var marker = Encoding.ASCII.GetString(bytes[..count]);
        if (count != 97 || marker[..64] != Owner(kind) || marker[64] != '.' ||
            !ImportStoragePhysicalLocation.IsIdentity(marker[65..]))
            throw new InvalidDataException("The physical import root belongs to another journal or root kind.");
        return marker;
    }

    private string Owner(ImportStorageRootKind kind) => Convert.ToHexStringLower(
        SHA256.HashData(Encoding.UTF8.GetBytes(_owner + '\0' + kind)));
}
