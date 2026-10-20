// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Attachments.Abstractions;
using Honua.Core.Features.Attachments.Domain;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Db.Postgres.Features.Infrastructure;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Db.Postgres.Features.Attachments;

internal sealed partial class PostgresAttachmentStore
{
    public async Task<bool> HasLegacyAttachmentsAsync(int layerId, CancellationToken cancellationToken)
    {
        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        var tables = await ResolveTablesAsync(connection, null, true, cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"SELECT EXISTS (SELECT 1 FROM {tables.Attachments} WHERE layer_id = $1 AND import_source IS NULL AND attachment_origin IS NULL)", connection);
        command.Parameters.AddWithValue(layerId);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true;
    }

    public async Task<Attachment> UploadImportedAsync(
        int layerId, long featureId, string source, long sourceParentId, long sourceAttachmentId,
        Guid generation, string filename, string contentType, Stream content, string? keywords,
        CancellationToken cancellationToken)
    {
        var uploaded = await RequireFileStorage().UploadAsync(new FileUploadRequest
        {
            Content = content,
            FileName = SanitizeFileName(filename),
            ContentType = contentType,
            SizeBytes = content.CanSeek ? content.Length : null,
            Folder = BuildAttachmentFolder(layerId, featureId)
        }, cancellationToken).ConfigureAwait(false);
        if (!uploaded.Success || uploaded.File is null)
        {
            throw new InvalidOperationException(uploaded.ErrorMessage ?? "Attachment upload failed.");
        }

        var path = uploaded.File.FileId;
        Attachment result;
        try
        {
            await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var tables = await ResolveTablesAsync(connection, transaction, true, cancellationToken).ConfigureAwait(false);
            // Serialize source identity updates, including the case where no row exists yet.
            await using (var lease = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended($1, 0))", connection, transaction))
            {
                lease.Parameters.AddWithValue($"honua.attachment-import:{tables.Attachments}:{layerId}");
                await lease.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using (var retire = new NpgsqlCommand($"""
                INSERT INTO {tables.Cleanup} (storage_path, layer_id, feature_id)
                SELECT storage_path, layer_id, feature_id FROM {tables.Attachments}
                WHERE layer_id = $1 AND import_source = $2 AND import_parent_id = $3 AND import_attachment_id = $4
                  AND storage_path <> $5
                ON CONFLICT (storage_path) DO NOTHING
                """, connection, transaction))
            {
                retire.Parameters.AddWithValue(layerId);
                retire.Parameters.AddWithValue(source);
                retire.Parameters.AddWithValue(sourceParentId);
                retire.Parameters.AddWithValue(sourceAttachmentId);
                retire.Parameters.AddWithValue(path);
                await retire.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using (var upsert = new NpgsqlCommand($"""
                INSERT INTO {tables.Attachments}
                    (layer_id, feature_id, filename, content_type, size, storage_path, keywords, attachment_origin,
                     import_source, import_parent_id, import_attachment_id, import_generation)
                VALUES ($1, $2, $3, $4, $5, $6, $7, 'geoservices', $8, $9, $10, $11)
                ON CONFLICT (layer_id, import_source, import_parent_id, import_attachment_id) DO UPDATE
                SET feature_id = EXCLUDED.feature_id, filename = EXCLUDED.filename,
                    content_type = EXCLUDED.content_type, size = EXCLUDED.size,
                    storage_path = EXCLUDED.storage_path, keywords = EXCLUDED.keywords,
                    import_generation = EXCLUDED.import_generation
                RETURNING id, feature_id, layer_id, filename, content_type, size, created_at, storage_path, keywords
                """, connection, transaction))
            {
                upsert.Parameters.AddWithValue(layerId);
                upsert.Parameters.AddWithValue(featureId);
                upsert.Parameters.AddWithValue(SanitizeFileName(filename));
                upsert.Parameters.AddWithValue(uploaded.File.ContentType);
                upsert.Parameters.AddWithValue(uploaded.File.SizeBytes);
                upsert.Parameters.AddWithValue(path);
                upsert.Parameters.AddWithValue(NpgsqlDbType.Text, keywords ?? (object)DBNull.Value);
                upsert.Parameters.AddWithValue(source);
                upsert.Parameters.AddWithValue(sourceParentId);
                upsert.Parameters.AddWithValue(sourceAttachmentId);
                upsert.Parameters.AddWithValue(generation);
                await using var reader = await upsert.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    throw new InvalidOperationException("Failed to save imported attachment.");
                }

                result = ReadAttachment(reader);
            }

            await transaction.CommitSafelyAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Queue instead of directly deleting: a lost commit acknowledgement can mean the
            // new row is already live. Cleanup checks references before touching object storage.
            try
            {
                await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(CancellationToken.None).ConfigureAwait(false);
                var tables = await ResolveTablesAsync(connection, null, true, CancellationToken.None).ConfigureAwait(false);
                await using var queue = new NpgsqlCommand($"""
                    INSERT INTO {tables.Cleanup} (storage_path, layer_id, feature_id)
                    VALUES ($1, $2, $3) ON CONFLICT (storage_path) DO NOTHING
                    """, connection);
                queue.Parameters.AddWithValue(path);
                queue.Parameters.AddWithValue(layerId);
                queue.Parameters.AddWithValue(featureId);
                await queue.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
                await DrainImportCleanupAsync(layerId).ConfigureAwait(false);
            }
            catch (Exception cleanupException) when (cleanupException is not OutOfMemoryException)
            {
                await RecordOrphanAsync(path, layerId, featureId, AttachmentOrphanKind.ObjectWithoutMetadata,
                    $"Imported attachment write failed ({ex.GetType().Name}); cleanup could not be queued ({cleanupException.GetType().Name}).").ConfigureAwait(false);
            }

            throw;
        }

        // Cleanup cannot turn a committed metadata write into a failed copy.
        await DrainImportCleanupAsync(layerId).ConfigureAwait(false);
        return result;
    }

    public async Task RemapImportedAttachmentsAsync(
        int layerId,
        IReadOnlyDictionary<long, long> sourceObjectIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sourceObjectIds);
        if (sourceObjectIds.Count == 0)
        {
            return;
        }

        var parents = new long[sourceObjectIds.Count];
        var targets = new long[sourceObjectIds.Count];
        var index = 0;
        foreach (var pair in sourceObjectIds)
        {
            parents[index] = pair.Key;
            targets[index] = pair.Value;
            index++;
        }

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var tables = await ResolveTablesAsync(connection, transaction, true, cancellationToken).ConfigureAwait(false);
        await using (var lease = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended($1, 0))", connection, transaction))
        {
            lease.Parameters.AddWithValue($"honua.attachment-import:{tables.Attachments}:{layerId}");
            await lease.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var remap = new NpgsqlCommand($"""
            UPDATE {tables.Attachments} AS attachment
            SET feature_id = mapped.target_id
            FROM unnest($2::bigint[], $3::bigint[]) AS mapped(parent_id, target_id)
            WHERE attachment.layer_id = $1
              AND attachment.import_parent_id = mapped.parent_id
              AND attachment.feature_id IS DISTINCT FROM mapped.target_id
            """, connection, transaction))
        {
            AddLayerAndObjectIdMap(remap, layerId, parents, targets);
            await remap.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        // A parent that was not loaded still carries its pre-swap feature id. When that id was
        // assigned to a different loaded feature, leave the attachment off every live feature.
        await using (var detach = new NpgsqlCommand($"""
            UPDATE {tables.Attachments} AS attachment
            SET feature_id = -attachment.id
            WHERE attachment.layer_id = $1
              AND attachment.import_parent_id IS NOT NULL
              AND attachment.feature_id > 0
              AND attachment.feature_id = ANY($2::bigint[])
              AND NOT EXISTS (
                  SELECT 1
                  FROM unnest($3::bigint[], $2::bigint[]) AS mapped(parent_id, target_id)
                  WHERE mapped.parent_id = attachment.import_parent_id
                    AND mapped.target_id = attachment.feature_id)
            """, connection, transaction))
        {
            AddLayerAndObjectIdMap(detach, layerId, targets, parents);
            await detach.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitSafelyAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void AddLayerAndObjectIdMap(NpgsqlCommand command, int layerId, long[] first, long[] second)
    {
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Integer, Value = layerId });
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bigint, Value = first });
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bigint, Value = second });
    }

    public async Task<bool> CompleteImportAsync(int layerId, Guid generation, CancellationToken cancellationToken)
    {
        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var tables = await ResolveTablesAsync(connection, transaction, true, cancellationToken).ConfigureAwait(false);
        await using (var lease = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended($1, 0))", connection, transaction))
        {
            lease.Parameters.AddWithValue($"honua.attachment-import:{tables.Attachments}:{layerId}");
            await lease.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var retire = new NpgsqlCommand($"""
            WITH retired AS (
                DELETE FROM {tables.Attachments}
                WHERE layer_id = $1 AND import_source IS NOT NULL AND import_generation <> $2
                RETURNING storage_path, layer_id, feature_id
            )
            INSERT INTO {tables.Cleanup} (storage_path, layer_id, feature_id)
            SELECT storage_path, layer_id, feature_id FROM retired
            ON CONFLICT (storage_path) DO NOTHING
            """, connection, transaction))
        {
            retire.Parameters.AddWithValue(layerId);
            retire.Parameters.AddWithValue(generation);
            await retire.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        bool hasUntracked;
        await using (var legacy = new NpgsqlCommand($"SELECT EXISTS (SELECT 1 FROM {tables.Attachments} WHERE layer_id = $1 AND import_source IS NULL AND attachment_origin IS NULL)", connection, transaction))
        {
            legacy.Parameters.AddWithValue(layerId);
            hasUntracked = await legacy.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true;
        }

        await transaction.CommitSafelyAsync(cancellationToken).ConfigureAwait(false);
        await DrainImportCleanupAsync(layerId).ConfigureAwait(false);
        return hasUntracked;
    }

    private async Task DrainImportCleanupAsync(int layerId)
    {
        try
        {
            await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(CancellationToken.None).ConfigureAwait(false);
            var tables = await ResolveTablesAsync(connection, null, true, CancellationToken.None).ConfigureAwait(false);
            var pending = new List<(string Path, long FeatureId)>();
            await using (var command = new NpgsqlCommand($"SELECT storage_path, feature_id FROM {tables.Cleanup} WHERE layer_id = $1", connection))
            {
                command.Parameters.AddWithValue(layerId);
                await using var reader = await command.ExecuteReaderAsync(CancellationToken.None).ConfigureAwait(false);
                while (await reader.ReadAsync(CancellationToken.None).ConfigureAwait(false))
                {
                    pending.Add((reader.GetString(0), reader.GetInt64(1)));
                }
            }

            foreach (var item in pending)
            {
                try
                {
                    await using var referenced = new NpgsqlCommand($"SELECT EXISTS (SELECT 1 FROM {tables.Attachments} WHERE storage_path = $1)", connection);
                    referenced.Parameters.AddWithValue(item.Path);
                    var isReferenced = await referenced.ExecuteScalarAsync(CancellationToken.None).ConfigureAwait(false) is true;
                    if (!isReferenced)
                    {
                        await RequireFileStorage().DeleteAsync(item.Path, CancellationToken.None).ConfigureAwait(false);
                    }

                    await using var remove = new NpgsqlCommand($"DELETE FROM {tables.Cleanup} WHERE storage_path = $1", connection);
                    remove.Parameters.AddWithValue(item.Path);
                    await remove.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    await RecordOrphanAsync(item.Path, layerId, item.FeatureId, AttachmentOrphanKind.UndeletedObject,
                        $"Imported attachment cleanup queued for retry ({ex.GetType().Name}).").ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AttachmentLog.AttachmentCleanupFailed(_logger, ex, "import_attachment_cleanup");
        }
    }
}
