// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.FileStorage;

namespace Honua.Import.FileImport.Resumable;

// Verifies raw filesystem entries, including unpublished metadata and partial
// content. Provider listings are deliberately not used. The caller must hold
// the exclusive domain fence for the complete census and ledger publication.
internal sealed class LocalImportStorageCensus
{
    internal const int MaximumLocalObjects = 65536;
    private readonly LocalImportStorageJournal _journal;
    private readonly Dictionary<ImportStorageRootKind, string> _roots;
    private readonly int _maximumObjects;
    private readonly LocalImportStorageRoots? _rootBindings;

    internal LocalImportStorageCensus(LocalImportStorageJournal journal,
        IReadOnlyDictionary<ImportStorageRootKind, string> roots, int maximumObjects, LocalImportStorageRoots? rootBindings = null)
    {
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(roots);
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Local import census requires a Linux host domain.");
        if (roots.Count is < 1 or > 3 || maximumObjects is < 1 or > MaximumLocalObjects)
            throw new ArgumentException("A local census requires bounded host roots and object capacity.");
        _journal = journal;
        _maximumObjects = maximumObjects;
        _rootBindings = rootBindings;
        _roots = [];
        foreach (var (kind, path) in roots)
        {
            if (!Enum.IsDefined(kind) || string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
                throw new ArgumentException("Every census root must be an absolute host-owned directory.");
            var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            if (fullPath == Path.GetPathRoot(fullPath) || Overlap(fullPath, journal.DirectoryPath) ||
                _roots.Values.Any(existing => Overlap(existing, fullPath)))
                throw new ArgumentException("Census roots and the control journal must be disjoint.");
            RequireDirectory(fullPath);
            _roots.Add(kind, fullPath);
        }
    }

    // Used only for initial bootstrap. Missing, inaccessible and nonempty roots
    // cannot be interpreted as a fresh empty storage domain.
    internal void VerifyEmpty(CancellationToken cancellationToken = default)
    {
        foreach (var (kind, root) in _roots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequireDirectory(root);
            var pending = new Stack<string>();
            pending.Push(root);
            var entries = 0;
            while (pending.TryPop(out var directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var path in Directory.EnumerateFileSystemEntries(directory))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (IsRootMarker(kind, root, path)) continue;
                    var attributes = File.GetAttributes(path);
                    if (++entries > checked(16 * _maximumObjects + 8) ||
                        (attributes & FileAttributes.Directory) == 0 || (attributes & FileAttributes.ReparsePoint) != 0)
                        throw new InvalidDataException("A new import journal requires physically empty data roots.");
                    // Providers can create their empty metadata directory before
                    // bootstrap. Empty canonical layout directories hold no data.
                    RequireLayoutDirectory(kind, Path.GetRelativePath(root, path));
                    pending.Push(path);
                }
            }
        }
    }

    internal async Task VerifyAsync(CancellationToken cancellationToken = default)
    {
        var files = new Dictionary<(ImportStorageRootKind Root, string Path), ExpectedFile>();
        var identities = new HashSet<string>(StringComparer.Ordinal);
        await foreach (var entry in _journal.ReadRetainedAsync(cancellationToken).ConfigureAwait(false))
        {
            var reservation = entry.Reservation;
            if (!identities.Add(reservation.FileId) || identities.Count > _maximumObjects)
                throw new InvalidDataException("The journal exceeds the local census capacity or repeats an identity.");
            var location = reservation.PhysicalLocation
                ?? throw new InvalidDataException("A retained reservation has no durable physical binding.");
            location.Validate(reservation);
            if (!_roots.ContainsKey(location.Root))
                throw new InvalidDataException("A reservation belongs to an unconfigured physical root.");
            Add(location.Root, location.RelativePath, new(location.MaximumContentBytes, false));
            if (location.MetadataPath(reservation) is { } metadata)
                Add(location.Root, metadata, new(location.Root == ImportStorageRootKind.ProviderObjects ? 65536 : ImportProcessingScratch.ManifestMaximumBytes,
                    location.Root == ImportStorageRootKind.ProviderObjects));
        }

        // Every retained intent stays charged, even if no physical file exists.
        // Reclaiming absent objects is a separate fenced deletion operation.
        foreach (var (kind, root) in _roots)
        {
            RequireDirectory(root);
            var pending = new Stack<string>();
            var temporaryMetadata = new HashSet<string>(StringComparer.Ordinal);
            pending.Push(root);
            var entries = 0;
            while (pending.TryPop(out var directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var path in Directory.EnumerateFileSystemEntries(directory))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (IsRootMarker(kind, root, path)) continue;
                    if (++entries > checked(16 * _maximumObjects + 8))
                        throw new InvalidDataException("The physical domain exceeds its bounded directory census.");
                    var relative = Path.GetRelativePath(root, path);
                    var attributes = File.GetAttributes(path);
                    if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
                        throw new InvalidDataException("A physical domain contains a link or device.");
                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        RequireLayoutDirectory(kind, relative);
                        pending.Push(path);
                        continue;
                    }
                    if (!files.TryGetValue((kind, relative), out var expected))
                    {
                        // A crashed metadata publication can retain its bounded
                        // temp file. Its name still binds the exact journal object.
                        var metadata = TemporaryMetadataParent(relative);
                        if (kind != ImportStorageRootKind.ProviderObjects || metadata is null ||
                            !files.TryGetValue((kind, metadata), out expected) || !expected.ProviderMetadata ||
                            !temporaryMetadata.Add(metadata))
                            throw new InvalidDataException("An unaccounted physical object prevents storage recovery.");
                    }
                    if (LocalStorageDurability.ReadRegularFileLength(path) > expected.MaximumBytes)
                        throw new InvalidDataException("Physical content exceeds its immutable storage reservation.");
                }
            }
        }

        void Add(ImportStorageRootKind root, string path, ExpectedFile expected)
        {
            if (!files.TryAdd((root, path), expected))
                throw new InvalidDataException("Two retained reservations claim the same physical path.");
        }
    }

    internal bool VerifyDeleted(ImportStorageReservation reservation)
    {
        RedisImportStorageBudget.Validate(reservation);
        var location = reservation.PhysicalLocation
            ?? throw new InvalidDataException("Deletion requires the durable physical binding.");
        if (!_roots.TryGetValue(location.Root, out var root))
            throw new InvalidDataException("The physical binding belongs to an unconfigured root.");
        RequireDirectory(root);
        if (!RequireDurableAbsence(Path.Join(root, location.RelativePath), root)) return false;
        if (location.MetadataPath(reservation) is not { } relativeMetadata) return true;
        var metadata = Path.Join(root, relativeMetadata);
        if (!RequireDurableAbsence(metadata, root)) return false;
        if (location.Root != ImportStorageRootKind.ProviderObjects) return true;
        var parent = Path.GetDirectoryName(metadata)!;
        try { _ = File.GetAttributes(parent); }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException) { return true; }
        // Even malformed temporary names with this object's exact hash prefix
        // prevent release. Unknown publication outcomes keep their charge.
        return !Directory.EnumerateFileSystemEntries(parent, Path.GetFileName(metadata) + ".*").Any();
    }

    private bool IsRootMarker(ImportStorageRootKind kind, string root, string path)
    {
        if (_rootBindings is null || path != Path.Join(root, LocalImportStorageRoots.MarkerName)) return false;
        _ = _rootBindings.ReadMarker(kind);
        return true;
    }

    internal void RequireBinding(ImportStorageReservation reservation)
    {
        RedisImportStorageBudget.Validate(reservation);
        if (reservation.PhysicalLocation is not { } location || !_roots.TryGetValue(location.Root, out var root))
            throw new InvalidDataException("Storage admission requires a physical binding to a configured host root.");
        RequireDirectory(root);
    }

    private static bool RequireDurableAbsence(string path, string root)
    {
        try { _ = File.GetAttributes(path); return false; }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException) { }
        for (var parent = Path.GetDirectoryName(path); parent is not null; parent = Path.GetDirectoryName(parent))
        {
            try
            {
                var attributes = File.GetAttributes(parent);
                if ((attributes & FileAttributes.Directory) == 0 || (attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Deletion cannot be proved through an aliased or non-directory parent.");
            }
            catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
            {
                if (parent == root) throw new IOException("The configured storage root disappeared.", exception);
                continue;
            }
            // Flush the surviving ancestry, including removal of missing nested
            // directories. A permission or I/O error is never proof of absence.
            LocalStorageDurability.FlushDirectoryTree(parent, root);
            return true;
        }
        throw new IOException("The configured storage boundary could not be verified.");
    }

    private static string? TemporaryMetadataParent(string relative)
    {
        if (relative.Length < 37 || !relative.EndsWith(".tmp", StringComparison.Ordinal) || relative[^37] != '.' ||
            !ImportStoragePhysicalLocation.IsIdentity(relative[^36..^4])) return null;
        return relative[..^37];
    }

    private static void RequireLayoutDirectory(ImportStorageRootKind root, string relative)
    {
        var segments = relative.Split('/');
        if (relative.Length > 1280 || segments.Any(part => part is "" or "." or ".." || part.Any(char.IsControl)))
            throw new InvalidDataException("A physical directory is outside the bounded storage layout.");
        var valid = root switch
        {
            ImportStorageRootKind.ProviderObjects => relative is "imports" or "imports/resumable" or ".metadata" ||
                segments.Length == 3 && segments[0] == "imports" && segments[1] == "resumable" &&
                ImportStoragePhysicalLocation.IsIdentity(segments[2]),
            ImportStorageRootKind.ProcessingScratch => IsProcessingDirectory(segments),
            _ => false
        };
        if (!valid) throw new InvalidDataException("A physical directory does not match its configured storage layout.");
    }

    private static bool IsProcessingDirectory(string[] segments)
    {
        var workspace = segments[0].Split('-');
        return segments.Length <= 9 && workspace.Length == 4 && workspace[0] == "honua" && workspace[1] == "processing" &&
            ImportStoragePhysicalLocation.IsIdentity(workspace[2]) && ImportStoragePhysicalLocation.IsIdentity(workspace[3]) &&
            (!segments.Contains(".reservations") || segments.Length == 2 && segments[1] == ".reservations");
    }

    private static bool Overlap(string left, string right) => left == right ||
        left.StartsWith(right + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
        right.StartsWith(left + Path.DirectorySeparatorChar, StringComparison.Ordinal);

    internal static void RequireDirectory(string directory)
    {
        for (var current = directory; current is not null; current = Path.GetDirectoryName(current))
        {
            var attributes = File.GetAttributes(current);
            if ((attributes & FileAttributes.Directory) == 0 || (attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("A physical root and its ancestors must be real host-owned directories.");
        }
    }

    private readonly record struct ExpectedFile(long MaximumBytes, bool ProviderMetadata);
}
