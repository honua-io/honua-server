// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Import.FileImport.Resumable;

/// <summary>
/// Reserves aggregate scratch capacity before allocating a private file. The host
/// binds the configured directory to one accounting domain for its actual volume;
/// all replicas sharing that volume must share that budget. Neither path nor budget
/// identity may be supplied by the caller. OS preallocation is an additional check,
/// not a replacement for accounting or reserved filesystem headroom.
/// </summary>
internal sealed class ImportScratchStorage
{
    private readonly string _directory;
    private readonly IImportStorageBudget _budget;

    internal ImportScratchStorage(string directory, IImportStorageBudget budget)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(budget);
        if (!Path.IsPathFullyQualified(directory))
            throw new ArgumentException("Scratch requires an absolute configured directory.", nameof(directory));
        _directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        _budget = budget;
    }

    internal async Task<ImportScratchLease> AllocateAsync(ImportUploadSession session, string directory,
        long maximumScratchBytes, CancellationToken cancellationToken = default)
    {
        RedisImportUploadStore.Validate(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (!Path.IsPathFullyQualified(directory) || !string.Equals(_directory,
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new ArgumentException("Scratch must use the directory bound to its storage budget.", nameof(directory));
        if (maximumScratchBytes < 1 || session.TotalBytes > maximumScratchBytes)
            throw new InvalidOperationException("The source does not fit the worker scratch allowance.");
        var id = Guid.NewGuid().ToString("N");
        var reservation = RedisImportStorageBudget.ForObject(session,
            $"imports/resumable/{session.Id}/scratch-{id}", session.TotalBytes);
        if (!await _budget.TryReserveAsync(reservation, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("The import scratch storage budget is exhausted.");

        // An unknown admission/allocation outcome remains charged for reconciliation.
        // There is deliberately no expiry that could release a still-running writer.
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(_directory);
        var path = Path.Join(_directory, $"honua-upload-{session.Id}-{id}.tmp");
        var file = new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.ReadWrite,
            Share = FileShare.None,
            Options = FileOptions.Asynchronous,
            PreallocationSize = session.TotalBytes
        });
        return new ImportScratchLease(file, path, _budget, reservation);
    }
}

/// <summary>
/// Owns a seekable scratch handle through verification and consumption. Cleanup
/// closes the writer, confirms deletion, then releases accounting without request
/// cancellation. Failed cleanup remains charged and requires reconciliation.
/// </summary>
internal sealed class ImportScratchLease(FileStream content, string path, IImportStorageBudget budget,
    ImportStorageReservation reservation) : IAsyncDisposable
{
    private readonly Lock _gate = new();
    private Task? _cleanup;

    // Only bounded canonical materializers write this stream. Import readers keep
    // this same handle; reopening a path after verification is not supported.
    internal FileStream Content { get; } = content;

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            return new ValueTask(_cleanup ??= CleanupAsync());
        }
    }

    private async Task CleanupAsync()
    {
        await Content.DisposeAsync().ConfigureAwait(false);
        File.Delete(path);
        _ = await budget.ReleaseDeletedAsync(reservation, CancellationToken.None).ConfigureAwait(false);
    }
}
