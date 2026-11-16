// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Npgsql;

namespace Honua.Db.Postgres.Features.Attachments;

internal interface IFeatureAttachmentCleanup
{
    Task<IReadOnlyList<DeletedAttachmentBlob>> DeleteRowsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        int layerId,
        long featureId,
        CancellationToken cancellationToken);

    Task DeleteBlobsAsync(
        IReadOnlyList<DeletedAttachmentBlob> blobs,
        CancellationToken cancellationToken);
}

internal readonly record struct DeletedAttachmentBlob(int LayerId, long FeatureId, string StoragePath);

internal sealed class PendingAttachmentBlobs
{
    private readonly List<DeletedAttachmentBlob> _paths = new();

    public int Count => _paths.Count;

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
