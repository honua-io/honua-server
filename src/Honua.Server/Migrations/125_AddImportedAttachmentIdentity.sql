-- Copyright (c) Honua. All rights reserved.
-- Licensed under the Elastic License 2.0. See LICENSE in the project root.

-- Existing rows remain untracked: source ownership cannot safely be inferred from filenames.
ALTER TABLE $HonuaSchema$.attachments
    ADD COLUMN attachment_origin TEXT CHECK (attachment_origin IN ('honua', 'geoservices')),
    ADD COLUMN import_source TEXT,
    ADD COLUMN import_parent_id BIGINT,
    ADD COLUMN import_attachment_id BIGINT,
    ADD COLUMN import_generation UUID,
    ADD CONSTRAINT ck_attachment_import_identity CHECK (
        (import_source IS NULL AND import_parent_id IS NULL AND import_attachment_id IS NULL AND import_generation IS NULL)
        OR (import_source IS NOT NULL AND import_parent_id IS NOT NULL AND import_attachment_id IS NOT NULL AND import_generation IS NOT NULL)),
    ADD CONSTRAINT uq_attachment_import_identity UNIQUE (layer_id, import_source, import_parent_id, import_attachment_id);

-- Set the default separately so existing rows stay unknown while subsequent ordinary
-- UploadAsync/CreateAsync writes are recognized as Honua-authored without guessing legacy ownership.
ALTER TABLE $HonuaSchema$.attachments ALTER COLUMN attachment_origin SET DEFAULT 'honua';

-- Retired objects are queued in the same transaction that retires their metadata. Retry
-- cleanup on the next import even if the process exits after commit or storage is unavailable.
CREATE TABLE $HonuaSchema$.import_attachment_cleanup (
    storage_path TEXT PRIMARY KEY,
    layer_id INT NOT NULL,
    feature_id BIGINT NOT NULL
);
