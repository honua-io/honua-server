// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Npgsql;

namespace Honua.Db.Postgres.Features.Attachments;

/// <summary>
/// Removes attachment metadata on the caller's feature-delete transaction, then the
/// backing blobs only after that transaction has committed (#6137).
/// </summary>
internal interface IFeatureAttachmentCleanup
{
    /// <summary>
    /// Deletes attachment rows for one feature on <paramref name="connection"/> and returns
    /// the storage paths whose blobs must not be removed until the transaction commits.
    /// </summary>
    Task<IReadOnlyList<DeletedAttachmentBlob>> DeleteRowsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        int layerId,
        long featureId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Deletes backing blobs. A blob that cannot be removed is recorded on the orphan ledger
    /// and does not fail the feature delete that already committed its metadata.
    /// </summary>
    Task DeleteBlobsAsync(
        IReadOnlyList<DeletedAttachmentBlob> blobs,
        CancellationToken cancellationToken);
}

/// <summary>
/// A storage object whose attachment row was deleted with a feature.
/// </summary>
/// <param name="LayerId">Layer that owned the attachment.</param>
/// <param name="FeatureId">Feature that owned the attachment.</param>
/// <param name="StoragePath">Storage key of the backing blob.</param>
internal readonly record struct DeletedAttachmentBlob(int LayerId, long FeatureId, string StoragePath);

/// <summary>
/// Holds blob paths for attachment rows deleted inside an open feature transaction.
/// A rollback discards them. A confirmed commit takes them once. An autocommit delete
/// does not stage them, so a later edit in the same batch cannot skip their cleanup.
/// </summary>
internal sealed class PendingAttachmentBlobs
{
    private readonly List<DeletedAttachmentBlob> _paths = new();

    public int Count => _paths.Count;

    /// <summary>
    /// Stages <paramref name="blobs"/> when <paramref name="batchTransactionOpen"/> is true.
    /// Otherwise returns them so the caller can delete the blobs after the row commit.
    /// </summary>
    public IReadOnlyList<DeletedAttachmentBlob>? Hold(
        IReadOnlyList<DeletedAttachmentBlob> blobs,
        bool batchTransactionOpen)
    {
        if (blobs.Count == 0)
        {
            return null;
        }

        if (batchTransactionOpen)
        {
            _paths.AddRange(blobs);
            return null;
        }

        return blobs;
    }

    public DeletedAttachmentBlob[] TakeForConfirmedCommit()
    {
        if (_paths.Count == 0)
        {
            return [];
        }

        var copy = _paths.ToArray();
        _paths.Clear();
        return copy;
    }

    public void Discard() => _paths.Clear();
}
