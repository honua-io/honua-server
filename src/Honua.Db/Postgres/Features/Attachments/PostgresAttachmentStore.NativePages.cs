// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Attachments.Domain;
using Npgsql;

namespace Honua.Db.Postgres.Features.Attachments;

internal sealed partial class PostgresAttachmentStore
{
    public async Task<IReadOnlyList<AttachmentPageItem>> QueryPageAsync(
        AttachmentPageQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(query.ContentTypes);
        ArgumentNullException.ThrowIfNull(query.Keywords);
        ArgumentOutOfRangeException.ThrowIfNegative(query.AfterId);
        ArgumentOutOfRangeException.ThrowIfLessThan(query.Limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(query.Limit, 501);
        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        var tables = await ResolveTablesAsync(connection, null, false, cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"""
            SELECT a.id, a.feature_id, a.layer_id, a.filename, a.content_type, a.size,
                a.created_at, a.storage_path, a.keywords, m.global_id, m.keywords, m.content_sha256
            FROM {tables.Attachments} a LEFT JOIN {NativeMetadataTable} m
                ON m.attachment_authority = @authority AND m.attachment_id = a.id
                AND m.layer_id = a.layer_id AND m.feature_id = a.feature_id AND m.storage_path = a.storage_path
            WHERE a.layer_id = @layer AND a.feature_id = @feature AND a.id > @after
                AND (cardinality(@types) = 0 OR a.content_type = ANY(@types))
                AND (cardinality(@keywords) = 0 OR
                    CASE WHEN m.global_id IS NULL THEN a.keywords = ANY(@keywords)
                    ELSE m.keywords && @keywords END)
            ORDER BY a.id LIMIT @limit
            """, connection);
        command.Parameters.AddWithValue("authority", tables.Attachments);
        command.Parameters.AddWithValue("layer", query.LayerId);
        command.Parameters.AddWithValue("feature", query.FeatureId);
        command.Parameters.AddWithValue("after", query.AfterId);
        command.Parameters.AddWithValue("limit", query.Limit);
        command.Parameters.AddWithValue("types", query.ContentTypes);
        command.Parameters.AddWithValue("keywords", query.Keywords);
        var result = new List<AttachmentPageItem>(query.Limit);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var metadata = reader.IsDBNull(9) ? null : new AttachmentNativeMetadata(
                reader.GetGuid(9), reader.GetFieldValue<string[]>(10), reader.GetFieldValue<byte[]>(11));
            result.Add(new AttachmentPageItem(ReadAttachment(reader), metadata));
        }
        return result;
    }
}
