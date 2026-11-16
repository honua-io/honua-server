// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Attachments.Domain;
using Npgsql;

namespace Honua.Db.Postgres.Features.Attachments;

internal sealed partial class PostgresAttachmentStore
{
    internal async Task DeleteWithCleanupAsync(AttachmentPublicationTransaction database, AttachmentDeleteRequest request)
    {
        var (connection, transaction, cancellationToken) = database;
        var tables = await ResolveTablesAsync(connection, transaction, true, cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"""
            WITH removed AS (
                DELETE FROM {tables.Attachments}
                WHERE id = @id AND layer_id = @layer AND feature_id = @feature
                RETURNING storage_path, layer_id, feature_id
            ), queued AS (
                INSERT INTO {tables.Cleanup} (storage_path, layer_id, feature_id)
                SELECT storage_path, layer_id, feature_id FROM removed
                ON CONFLICT (storage_path) DO NOTHING
            )
            SELECT count(*) FROM removed
            """, connection, transaction);
        command.Parameters.AddWithValue("id", request.AttachmentId);
        command.Parameters.AddWithValue("layer", request.LayerId);
        command.Parameters.AddWithValue("feature", request.FeatureId);
        if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not 1L)
        {
            throw new AttachmentUploadException(AttachmentUploadFailure.NotFound, "Attachment was not found on this feature.");
        }
    }
}
