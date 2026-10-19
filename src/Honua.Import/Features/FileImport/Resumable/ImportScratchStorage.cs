// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.FileStorage;

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
    private readonly IStorageDomainMutationFence? _domainFence;

    internal ImportScratchStorage(string directory, IImportStorageBudget budget, IStorageDomainMutationFence? domainFence = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(budget);
        if (!Path.IsPathFullyQualified(directory))
            throw new ArgumentException("Scratch requires an absolute configured directory.", nameof(directory));
        _directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        _budget = budget;
        _domainFence = domainFence;
    }

    internal bool IsBoundToMutationFence(IStorageDomainMutationFence fence) => ReferenceEquals(_domainFence, fence);

    internal bool IsBoundToDirectory(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory)) return false;
        try
        {
            return string.Equals(_directory, Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    internal Task<ImportScratchLease> AllocateAsync(ImportUploadSession session, string directory,
        long maximumScratchBytes, CancellationToken cancellationToken = default)
    {
        RedisImportUploadStore.Validate(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (!IsBoundToDirectory(directory))
            throw new ArgumentException("Scratch must use the directory bound to its storage budget.", nameof(directory));
        return AllocateAsync(session.Id, session.Scope, session.TotalBytes, maximumScratchBytes, cancellationToken);
    }

    internal Task<ImportScratchLease> AllocateAsync(ImportTransferProvenance transfer, long bytes,
        long maximumScratchBytes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transfer);
        transfer.Validate();
        return AllocateAsync(transfer.UploadId, new ImportUploadScope(transfer.OwnerActorId, transfer.TenantId,
            transfer.TargetId, transfer.WorkspaceId, transfer.TaskId, transfer.TaskRevision), bytes, maximumScratchBytes, cancellationToken);
    }

    private async Task<ImportScratchLease> AllocateAsync(string uploadId, ImportUploadScope scope, long bytes,
        long maximumScratchBytes, CancellationToken cancellationToken)
    {
        _ = RedisImportUploadStore.Key(uploadId);
        RedisImportUploadStore.ValidateScope(scope);
        if (maximumScratchBytes < 1 || bytes < 1 || bytes > maximumScratchBytes || bytes > RedisImportUploadStore.MaximumFileBytes)
            throw new InvalidOperationException("The source does not fit the worker scratch allowance.");
        var id = Guid.NewGuid().ToString("N");
        var reservation = new ImportStorageReservation(uploadId, scope, $"imports/resumable/{uploadId}/scratch-{id}", bytes);
        var mutationLease = _domainFence is null ? null
            : await _domainFence.EnterMutationAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!await _budget.TryReserveAsync(reservation, cancellationToken).ConfigureAwait(false))
                throw new InvalidOperationException("The import scratch storage budget is exhausted.");

            // An unknown admission/allocation outcome remains charged for reconciliation.
            // There is deliberately no expiry that could release a still-running writer.
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(_directory);
            var path = Path.Join(_directory, $"honua-upload-{uploadId}-{id}.tmp");
            var file = new FileStream(path, new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.ReadWrite,
                Share = FileShare.None,
                Options = FileOptions.Asynchronous,
                PreallocationSize = bytes
            });
            return new ImportScratchLease(file, path, _budget, reservation, _domainFence, mutationLease);
        }
        catch
        {
            if (mutationLease is not null) await mutationLease.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}

/// <summary>
/// Owns a seekable scratch handle through verification and consumption. Cleanup
/// closes the writer, confirms deletion, then releases accounting without request
/// cancellation. Failed cleanup remains charged and requires reconciliation.
/// </summary>
internal sealed class ImportScratchLease(FileStream content, string path, IImportStorageBudget budget,
    ImportStorageReservation reservation, IStorageDomainMutationFence? domainFence = null,
    IAsyncDisposable? mutationLease = null) : IAsyncDisposable
{
    private readonly Lock _gate = new();
    private Task? _cleanup;
    private IAsyncDisposable? _mutationLease = mutationLease;

    // Only bounded canonical materializers write this stream. Import readers keep
    // this same handle; reopening a path after verification is not supported.
    internal FileStream Content { get; } = content;

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_cleanup is null || _cleanup.IsFaulted || _cleanup.IsCanceled) _cleanup = CleanupAsync();
            return new ValueTask(_cleanup);
        }
    }

    private async Task CleanupAsync()
    {
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            if (_mutationLease is null && domainFence is not null)
                _mutationLease = await domainFence.EnterMutationAsync(cleanup.Token).ConfigureAwait(false);
            await Content.DisposeAsync().ConfigureAwait(false);
            File.Delete(path);
            if (domainFence is not null)
            {
                // Deletion must survive a host restart before its capacity can
                // be reused. A failed fsync retains the charge for reconciliation.
                var parent = Path.GetDirectoryName(path)!;
                LocalStorageDurability.FlushDirectoryTree(parent, parent);
            }
            _ = await budget.ReleaseDeletedAsync(reservation, cleanup.Token).ConfigureAwait(false);
        }
        finally
        {
            // A failed cleanup stays charged but must release its shared lease so
            // exclusive census can recover it. Retrying reacquires the domain fence.
            if (_mutationLease is { } lease)
            {
                _mutationLease = null;
                await lease.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
