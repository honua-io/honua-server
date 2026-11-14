// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Attachments.Domain;
using Honua.Db.Postgres.Features.Infrastructure;
using Npgsql;

namespace Honua.Db.Postgres.Features.Attachments;

internal sealed partial class PostgresAttachmentStore
{
    private string NativeMetadataTable => SchemaSearchPath.QualifyTable("attachment_native_metadata", _canonicalSchema);

    internal async Task RecordNativeMetadataAsync(AttachmentPublicationTransaction database, Attachment attachment, AttachmentUploadRequest request)
    {
        var (connection, transaction, cancellationToken) = database;
        var tables = await ResolveTablesAsync(connection, transaction, false, cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"""
            INSERT INTO {NativeMetadataTable} AS current
                (attachment_authority, attachment_id, layer_id, feature_id, storage_path, keywords, content_sha256)
            VALUES (@authority, @id, @layer, @feature, @path, @keywords, @digest)
            ON CONFLICT (attachment_authority, attachment_id) DO UPDATE
                SET layer_id = EXCLUDED.layer_id, feature_id = EXCLUDED.feature_id,
                    storage_path = EXCLUDED.storage_path, keywords = EXCLUDED.keywords,
                    content_sha256 = EXCLUDED.content_sha256
            """, connection, transaction);
        command.Parameters.AddWithValue("authority", tables.Attachments);
        command.Parameters.AddWithValue("id", attachment.Id);
        command.Parameters.AddWithValue("layer", attachment.LayerId);
        command.Parameters.AddWithValue("feature", attachment.FeatureId);
        command.Parameters.AddWithValue("path", attachment.StoragePath);
        command.Parameters.AddWithValue("keywords", request.Keywords);
        command.Parameters.AddWithValue("digest", request.Sha256);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyDictionary<long, AttachmentNativeMetadata>> GetNativeMetadataAsync(
        AttachmentNativeMetadataQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(query.AttachmentIds);
        var result = new Dictionary<long, AttachmentNativeMetadata>();
        if (query.AttachmentIds.Length == 0)
        {
            return result;
        }
        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        var tables = await ResolveTablesAsync(connection, null, false, cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"""
            SELECT m.attachment_id, m.global_id, m.keywords, m.content_sha256
            FROM {NativeMetadataTable} m JOIN {tables.Attachments} a
                ON a.id = m.attachment_id AND a.layer_id = m.layer_id AND a.feature_id = m.feature_id
                AND a.storage_path = m.storage_path
            WHERE m.attachment_authority = @authority AND m.layer_id = @layer
                AND m.feature_id = @feature AND m.attachment_id = ANY(@ids)
            """, connection);
        command.Parameters.AddWithValue("authority", tables.Attachments);
        command.Parameters.AddWithValue("layer", query.LayerId);
        command.Parameters.AddWithValue("feature", query.FeatureId);
        command.Parameters.AddWithValue("ids", query.AttachmentIds);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(reader.GetInt64(0), new AttachmentNativeMetadata(reader.GetGuid(1), reader.GetFieldValue<string[]>(2), reader.GetFieldValue<byte[]>(3)));
        }
        return result;
    }
}
