-- Copyright (c) Honua. All rights reserved.
-- Licensed under the Elastic License 2.0. See LICENSE in the project root.

-- A collaboration map id is the canonical Studio draft UUID rendered as text. Serialize every
-- UUID-backed operation-log head insertion against deletion of that draft. BEFORE INSERT also
-- runs for INSERT ... ON CONFLICT DO NOTHING, so normal appends lock the draft row before they
-- lock or create the head. A concurrent delete therefore either waits and removes the committed
-- log, or wins first and makes the insert fail closed. Non-UUID ids remain available to the
-- standalone operation-log repository.
CREATE OR REPLACE FUNCTION $HonuaSchema$.require_studio_draft_for_saved_map_operation_log_head()
RETURNS trigger
LANGUAGE plpgsql
AS '
DECLARE
    draft_uuid uuid;
BEGIN
    BEGIN
        draft_uuid := NEW.map_id::uuid;
    EXCEPTION
        WHEN invalid_text_representation THEN
            RETURN NEW;
    END;

    PERFORM 1
    FROM $HonuaSchema$.studio_package_drafts
    WHERE draft_id = draft_uuid
    FOR KEY SHARE;

    IF NOT FOUND THEN
        RAISE foreign_key_violation USING
            MESSAGE = ''saved-map operation log requires an existing Studio draft'';
    END IF;

    RETURN NEW;
END;
';

DROP TRIGGER IF EXISTS trg_require_studio_draft_for_saved_map_operation_log_head
    ON $HonuaSchema$.saved_map_operation_log_heads;

CREATE TRIGGER trg_require_studio_draft_for_saved_map_operation_log_head
BEFORE INSERT ON $HonuaSchema$.saved_map_operation_log_heads
FOR EACH ROW
EXECUTE FUNCTION $HonuaSchema$.require_studio_draft_for_saved_map_operation_log_head();

-- Keep deletion of the draft and its durable operation log in the same PostgreSQL transaction so
-- a failed cleanup cannot strand payload rows and a failed draft delete cannot discard a live
-- draft's replay log. Deleting the head cascades to saved_map_operations and
-- saved_map_checkpoint_versions.
CREATE OR REPLACE FUNCTION $HonuaSchema$.delete_saved_map_operation_log_with_studio_draft()
RETURNS trigger
LANGUAGE plpgsql
AS '
BEGIN
    DELETE FROM $HonuaSchema$.saved_map_operation_log_heads
    WHERE map_id = OLD.draft_id::text;
    RETURN OLD;
END;
';

DROP TRIGGER IF EXISTS trg_delete_saved_map_operation_log_with_studio_draft
    ON $HonuaSchema$.studio_package_drafts;

CREATE TRIGGER trg_delete_saved_map_operation_log_with_studio_draft
AFTER DELETE ON $HonuaSchema$.studio_package_drafts
FOR EACH ROW
EXECUTE FUNCTION $HonuaSchema$.delete_saved_map_operation_log_with_studio_draft();
