-- Copyright (c) Honua. All rights reserved.
-- Licensed under the Elastic License 2.0. See LICENSE in the project root.

-- Pointer revisions detect intervening writes, including an ABA return to the same
-- saved version. Original guarded receipts survive a lost response or process restart.
ALTER TABLE $HonuaSchema$.studio_content_items
    ADD COLUMN IF NOT EXISTS pointer_revision bigint NOT NULL DEFAULT 0;

ALTER TABLE $HonuaSchema$.studio_rollback_requests
    ADD COLUMN IF NOT EXISTS pointer_revision bigint NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS preconditions jsonb;

-- The database owns the revision so a still-running previous binary also advances
-- it during a rolling deployment. Assign OLD + 1 even when a writer supplies a value.
CREATE OR REPLACE FUNCTION $HonuaSchema$.advance_studio_pointer_revision()
RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    NEW.pointer_revision := OLD.pointer_revision + 1;
    RETURN NEW;
END;
$$;

DROP TRIGGER IF EXISTS studio_pointer_revision ON $HonuaSchema$.studio_content_items;
CREATE TRIGGER studio_pointer_revision
    BEFORE UPDATE OF current_version_id, published_version_id
    ON $HonuaSchema$.studio_content_items
    FOR EACH ROW EXECUTE FUNCTION $HonuaSchema$.advance_studio_pointer_revision();
