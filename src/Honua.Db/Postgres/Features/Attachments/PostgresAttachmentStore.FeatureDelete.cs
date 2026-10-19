// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Attachments.Abstractions;
using Npgsql;

namespace Honua.Db.Postgres.Features.Attachments;

internal sealed partial class PostgresAttachmentStore
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<DeletedAttachmentBlob>> DeleteRowsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        int layerId,
        long featureId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var sql = $@"
            DELETE FROM {_tableName}
            WHERE layer_id = $1 AND feature_id = $2
            RETURNING storage_path";

        await using var command = new NpgsqlCommand(sql, connection)
        {
            Transaction = transaction,
        };
        command.Parameters.AddWithValue(layerId);
        command.Parameters.AddWithValue(featureId);

        var blobs = new List<DeletedAttachmentBlob>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (reader.IsDBNull(0))
            {
                continue;
            }

            blobs.Add(new DeletedAttachmentBlob(layerId, featureId, reader.GetString(0)));
        }

        return blobs;
    }

    /// <inheritdoc />
    public async Task DeleteBlobsAsync(
        IReadOnlyList<DeletedAttachmentBlob> blobs,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(blobs);

        foreach (var blob in blobs)
        {
            if (string.IsNullOrEmpty(blob.StoragePath))
            {
                continue;
            }

            try
            {
                var deleted = await RequireFileStorage().DeleteAsync(blob.StoragePath, cancellationToken).ConfigureAwait(false);
                if (!deleted)
                {
                    // false means the object is already gone. The row is gone too, so the
                    // stores agree and an orphan record would be a false positive.
                    AttachmentLog.AttachmentFileMissing(_logger, blob.StoragePath, blob.LayerId, blob.FeatureId);
                }
            }
            catch (OperationCanceledException ex)
            {
                await RecordOrphanAsync(
                    blob.StoragePath,
                    blob.LayerId,
                    blob.FeatureId,
                    AttachmentOrphanKind.UndeletedObject,
                    $"storage delete was canceled after the attachment row was committed away ({ex.GetType().Name})").ConfigureAwait(false);
            }
            // codeql[cs/catch-of-all-exceptions]: any storage failure becomes an orphan record so the committed feature delete is not failed
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                AttachmentLog.AttachmentFileDeleteFailed(_logger, ex, blob.StoragePath);
                await RecordOrphanAsync(
                    blob.StoragePath,
                    blob.LayerId,
                    blob.FeatureId,
                    AttachmentOrphanKind.UndeletedObject,
                    ex.Message).ConfigureAwait(false);
            }
        }
    }
}
