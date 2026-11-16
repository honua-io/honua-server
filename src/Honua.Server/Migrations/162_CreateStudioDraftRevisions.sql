-- Copyright (c) Honua Geo Inc. All rights reserved.
-- Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

CREATE TABLE IF NOT EXISTS $HonuaSchema$.studio_draft_revisions (
    draft_id UUID NOT NULL REFERENCES $HonuaSchema$.studio_package_drafts(draft_id) ON DELETE CASCADE,
    generation BIGINT NOT NULL,
    envelope JSONB NOT NULL,
    updated_by TEXT,
    updated_at TIMESTAMPTZ NOT NULL,
    PRIMARY KEY (draft_id, generation)
);

-- Capture OLD in the writer's transaction, including writers running an older server image.
CREATE OR REPLACE FUNCTION $HonuaSchema$.archive_studio_draft_revision()
RETURNS trigger
LANGUAGE plpgsql
AS '
BEGIN
    INSERT INTO $HonuaSchema$.studio_draft_revisions
        (draft_id, generation, envelope, updated_by, updated_at)
    VALUES (OLD.draft_id, OLD.generation, OLD.envelope, OLD.updated_by, OLD.updated_at);
    RETURN NEW;
END;
';

CREATE OR REPLACE TRIGGER trg_archive_studio_draft_revision
AFTER UPDATE ON $HonuaSchema$.studio_package_drafts
FOR EACH ROW
WHEN (OLD.generation IS DISTINCT FROM NEW.generation)
EXECUTE FUNCTION $HonuaSchema$.archive_studio_draft_revision();
