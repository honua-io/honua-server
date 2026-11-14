// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Attachments.Domain;
using Npgsql;

namespace Honua.Db.Postgres.Features.Attachments;

internal sealed partial class PostgresAttachmentStore
{
    internal async Task<Attachment> PublishUploadAsync(AttachmentPublicationTransaction database, Attachment attachment, long? replacementId)
    {
        await EnforceUploadQuotaAsync(database, attachment, replacementId).ConfigureAwait(false);
        if (replacementId is null)
        {
            return await InsertAttachmentAsync(database.Connection, database.Transaction, attachment.LayerId, attachment.FeatureId, attachment, database.CancellationToken).ConfigureAwait(false);
        }
        return await ReplaceUploadAsync(database, attachment, replacementId.Value).ConfigureAwait(false);
    }

    private async Task<Attachment> ReplaceUploadAsync(AttachmentPublicationTransaction database, Attachment attachment, long replacementId)
    {
        var (connection, transaction, cancellationToken) = database;
        var tables = await ResolveTablesAsync(connection, transaction, true, cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"""
            WITH previous AS (
                SELECT id, storage_path, layer_id, feature_id FROM {tables.Attachments}
                WHERE id = @id AND layer_id = @layer AND feature_id = @feature FOR UPDATE
            ), retired AS (
                INSERT INTO {tables.Cleanup} (storage_path, layer_id, feature_id)
                SELECT storage_path, layer_id, feature_id FROM previous WHERE storage_path <> @path
                ON CONFLICT (storage_path) DO NOTHING
            )
            UPDATE {tables.Attachments} AS a SET filename = @filename, content_type = @type,
                size = @size, storage_path = @path, keywords = @keywords, attachment_origin = 'honua'
            FROM previous WHERE a.id = previous.id
            RETURNING a.id, a.feature_id, a.layer_id, a.filename, a.content_type, a.size, a.created_at, a.storage_path, a.keywords
            """, connection, transaction);
        command.Parameters.AddWithValue("id", replacementId);
        command.Parameters.AddWithValue("layer", attachment.LayerId);
        command.Parameters.AddWithValue("feature", attachment.FeatureId);
        command.Parameters.AddWithValue("filename", attachment.Filename);
        command.Parameters.AddWithValue("type", attachment.ContentType);
        command.Parameters.AddWithValue("size", attachment.Size);
        command.Parameters.AddWithValue("path", attachment.StoragePath);
        command.Parameters.AddWithValue("keywords", NpgsqlTypes.NpgsqlDbType.Text, attachment.Keywords ?? (object)DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new AttachmentUploadException(AttachmentUploadFailure.NotFound, "Replacement attachment was not found on this feature.");
        }
        return ReadAttachment(reader);
    }
}
