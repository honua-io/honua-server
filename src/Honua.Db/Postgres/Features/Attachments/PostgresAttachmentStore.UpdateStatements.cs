// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Db.Postgres.Features.Attachments;

internal sealed partial class PostgresAttachmentStore
{
    private string CreateAttachmentUpdateSql(string attachmentsTable) => $@"
            WITH previous AS (
                SELECT id, keywords FROM {attachmentsTable}
                WHERE layer_id = $1 AND feature_id = $2 AND id = $3 FOR UPDATE
            ), updated AS (
                UPDATE {attachmentsTable} a
                SET filename = $4, content_type = $5, size = $6, storage_path = $7, keywords = $8
                FROM previous p WHERE a.id = p.id AND a.layer_id = $1 AND a.feature_id = $2
                RETURNING a.id, a.feature_id, a.layer_id, a.filename, a.content_type, a.size,
                    a.created_at, a.storage_path, a.keywords, p.keywords AS previous_keywords
            ), synced AS (
                UPDATE {NativeMetadataTable} m
                SET keywords = COALESCE(string_to_array(u.keywords, ','), ARRAY[]::text[])
                FROM updated u
                WHERE m.attachment_authority = $9 AND m.attachment_id = u.id
                    AND m.layer_id = u.layer_id AND m.feature_id = u.feature_id AND m.storage_path = u.storage_path
                    AND u.keywords IS DISTINCT FROM u.previous_keywords
            )
            SELECT * FROM updated";
}
