-- Copyright (c) Honua. All rights reserved.
-- Licensed under the Elastic License 2.0. See LICENSE in the project root.

-- Idempotent like the other seed-mirrored migrations: the migration-skipping test seed
-- (tests/seed/server.yaml) creates these objects, and fixtures that seed and then migrate
-- re-run this script. DbUp journals by script name, so databases that already applied the
-- first version of this script do not run it again; a fresh database ends in the same schema.

-- Existing rows remain untracked: source ownership cannot safely be inferred from filenames.
ALTER TABLE $HonuaSchema$.attachments
    ADD COLUMN IF NOT EXISTS attachment_origin TEXT CHECK (attachment_origin IN ('honua', 'geoservices')),
    ADD COLUMN IF NOT EXISTS import_source TEXT,
    ADD COLUMN IF NOT EXISTS import_parent_id BIGINT,
    ADD COLUMN IF NOT EXISTS import_attachment_id BIGINT,
    ADD COLUMN IF NOT EXISTS import_generation UUID;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'ck_attachment_import_identity'
          AND conrelid = to_regclass('$HonuaSchema$.attachments')) THEN
        ALTER TABLE $HonuaSchema$.attachments
            ADD CONSTRAINT ck_attachment_import_identity CHECK (
                (import_source IS NULL AND import_parent_id IS NULL AND import_attachment_id IS NULL AND import_generation IS NULL)
                OR (import_source IS NOT NULL AND import_parent_id IS NOT NULL AND import_attachment_id IS NOT NULL AND import_generation IS NOT NULL));
    END IF;
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'uq_attachment_import_identity'
          AND conrelid = to_regclass('$HonuaSchema$.attachments')) THEN
        ALTER TABLE $HonuaSchema$.attachments
            ADD CONSTRAINT uq_attachment_import_identity UNIQUE (layer_id, import_source, import_parent_id, import_attachment_id);
    END IF;
END $$;

-- Origin has no default: old binaries and legacy writers remain unknown. New Honua
-- attachment writes mark their origin explicitly; import writes mark GeoServices provenance.

-- Retired objects are queued in the same transaction that retires their metadata. Retry
-- cleanup on the next import even if the process exits after commit or storage is unavailable.
CREATE TABLE IF NOT EXISTS $HonuaSchema$.import_attachment_cleanup (
    storage_path TEXT PRIMARY KEY,
    layer_id INT NOT NULL,
    feature_id BIGINT NOT NULL
);
