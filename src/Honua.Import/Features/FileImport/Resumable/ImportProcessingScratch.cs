// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;

namespace Honua.Import.FileImport.Resumable;

internal sealed class ImportProcessingScratchFactory(string directory, IImportStorageBudget budget)
    : IImportProcessingScratchFactory
{
    public Task<IImportProcessingScratch> CreateAsync(ImportTransferProvenance transfer, long maximumBytes,
        int maximumFiles, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transfer);
        transfer.Validate();
        ArgumentNullException.ThrowIfNull(budget);
        if (!Path.IsPathFullyQualified(directory) || maximumBytes < 1 ||
            maximumBytes > RedisImportUploadStore.MaximumFileBytes || maximumFiles is < 1 or > 100000)
            throw new ArgumentException("Processing scratch requires a configured volume and bounded capacity.");
        cancellationToken.ThrowIfCancellationRequested();
        var root = Path.Join(Path.GetFullPath(directory), $"honua-processing-{transfer.UploadId}-{Guid.NewGuid():N}");
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(root);
        else Directory.CreateDirectory(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return Task.FromResult<IImportProcessingScratch>(new ImportProcessingScratch(root, transfer, budget, maximumBytes, maximumFiles));
    }
}

internal sealed class ImportProcessingScratch(string directory, ImportTransferProvenance transfer,
    IImportStorageBudget budget, long maximumBytes, int maximumFiles) : IImportProcessingScratch
{
    internal const int FileAccountingOverhead = 64 * 1024;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<ImportStorageReservation> _reservations = [];
    private readonly HashSet<string> _paths = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private long _reservedBytes;
    private bool _disposed;
    private bool _physicalDeleted;
    private bool _cleanupCompleted;

    public string DirectoryPath { get; } = directory;

    public async Task WriteFileAsync(Stream source, string path, long expectedBytes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (expectedBytes < 0) throw new ArgumentOutOfRangeException(nameof(expectedBytes));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var fullPath = ValidatePath(path);
            var chargedBytes = checked(Math.Max(1, expectedBytes) + FileAccountingOverhead);
            if (_reservations.Count >= maximumFiles || chargedBytes > maximumBytes - _reservedBytes)
                throw new InvalidOperationException("The processing scratch allowance is exhausted.");
            if (!_paths.Add(fullPath)) throw new IOException("A processing scratch path cannot be reused.");
            var id = Guid.NewGuid().ToString("N");
            var reservation = new ImportStorageReservation(transfer.UploadId,
                new ImportUploadScope(transfer.OwnerActorId, transfer.TenantId, transfer.TargetId,
                    transfer.WorkspaceId, transfer.TaskId, transfer.TaskRevision),
                $"imports/resumable/{transfer.UploadId}/scratch-{id}", chargedBytes);
            _reservations.Add(reservation);
            _reservedBytes += chargedBytes;

            // Reserve content plus bounded metadata/directory overhead before any file
            // write. A lost admission reply remains charged; no content is written then.
            if (!await budget.TryReserveAsync(reservation, cancellationToken).ConfigureAwait(false))
                throw new InvalidOperationException("The import storage budget is exhausted.");
            cancellationToken.ThrowIfCancellationRequested();
            // Persist the physical/accounting association before creating content. A
            // crash can leave a reservation without a file, never unaccounted content.
            var metadataDirectory = Path.Join(DirectoryPath, ".reservations");
            Directory.CreateDirectory(metadataDirectory);
            var manifest = JsonSerializer.SerializeToUtf8Bytes(
                new ImportProcessingScratchReservation(reservation, Path.GetRelativePath(DirectoryPath, fullPath)),
                ImportProcessingScratchJsonContext.Default.ImportProcessingScratchReservation);
            if (manifest.Length > 8192) throw new InvalidDataException("Processing scratch metadata exceeds its allowance.");
            await using (var metadata = new FileStream(Path.Join(metadataDirectory, id + ".json"),
                FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await metadata.WriteAsync(manifest, cancellationToken).ConfigureAwait(false);
                await metadata.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            await using var output = new FileStream(fullPath, new FileStreamOptions
            {
                Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan, PreallocationSize = expectedBytes
            });
            var buffer = new byte[64 * 1024];
            long written = 0;
            while (true)
            {
                var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                if (read > expectedBytes - written)
                    throw new InvalidDataException("Processing scratch content exceeds its reserved length.");
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                written += read;
            }
            if (written != expectedBytes)
                throw new InvalidDataException("Processing scratch content is shorter than its declared length.");
        }
        finally { _gate.Release(); }
    }

    private string ValidatePath(string path)
    {
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("An absolute processing path is required.", nameof(path));
        var fullPath = Path.GetFullPath(path);
        var relative = Path.GetRelativePath(DirectoryPath, fullPath);
        var segments = relative.Split(Path.DirectorySeparatorChar);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (relative == "." || relative.Length > 1024 || segments.Length > 8 ||
            Path.IsPathRooted(relative) || segments.Any(segment => segment == ".." || segment.Equals(".reservations", comparison)))
            throw new ArgumentException("Processing scratch paths must remain inside the private workspace.", nameof(path));
        for (var current = Path.GetDirectoryName(fullPath); current is not null; current = Path.GetDirectoryName(current))
        {
            if (Directory.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Processing scratch cannot traverse symbolic links.");
            if (current.Equals(DirectoryPath, comparison)) break;
        }
        return fullPath;
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_cleanupCompleted) return;
            _disposed = true;
            // Delete content before manifests. A failed content deletion must leave its
            // physical/accounting association available for later census. Exists() is not
            // proof of absence: it also returns false for inaccessible paths.
            if (!_physicalDeleted)
            {
                foreach (var path in _paths) File.Delete(path);
                try { Directory.Delete(DirectoryPath, recursive: true); }
                catch (DirectoryNotFoundException) { }
                _physicalDeleted = true;
            }
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            foreach (var reservation in _reservations)
                _ = await budget.ReleaseDeletedAsync(reservation, cleanup.Token).ConfigureAwait(false);
            _cleanupCompleted = true;
        }
        finally { _gate.Release(); }
    }
}

internal sealed record ImportProcessingScratchReservation(ImportStorageReservation Reservation, string RelativePath);

[JsonSerializable(typeof(ImportProcessingScratchReservation))]
internal sealed partial class ImportProcessingScratchJsonContext : JsonSerializerContext;
